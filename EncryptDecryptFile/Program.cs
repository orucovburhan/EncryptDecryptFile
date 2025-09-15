using System;
using System.IO;
using System.Text;
using System.Threading;

class FileXorCryptoWithProgress
{
    // Shared progress counters used by the worker and the progress thread
    static long totalBytes = 0;
    static long processedBytes = 0;
    static volatile bool processingDone = false;

    // New: cancellation flag set when user presses Enter
    static volatile bool cancelRequested = false;

    static void Main()
    {
        Console.WriteLine("=== Simple XOR File Encryptor/Decryptor (with progress + cancel) ===");

        // 1) Input file
        string inputPath;
        while (true)
        {
            Console.Write("Enter input file path: ");
            inputPath = Console.ReadLine() ?? "";
            if (File.Exists(inputPath)) break;
            Console.WriteLine("File not found. Try again.");
        }

        // Show first bytes of the input file for verification (hex)
        byte[] beforeSample = ReadSampleBytes(inputPath, 16);
        Console.WriteLine("First bytes of input (hex): " + BytesToHex(beforeSample));

        // 2) Key (0-255)
        byte key;
        while (true)
        {
            Console.Write("Enter key (integer 0-255): ");
            string? keyStr = Console.ReadLine();
            if (byte.TryParse(keyStr, out key)) break;
            Console.WriteLine("Invalid key. Please enter a number between 0 and 255.");
        }

        if (key == 0)
        {
            Console.WriteLine("Warning: key = 0 will NOT change the file (XOR with 0 is identity).");
            Console.Write("Do you want to continue with key 0? (y/n): ");
            string c = (Console.ReadLine() ?? "").Trim().ToLower();
            if (c != "y") { Console.WriteLine("Aborted."); return; }
        }

        // 3) Operation: encrypt or decrypt
        string operation;
        while (true)
        {
            Console.Write("Type 'e' to encrypt or 'd' to decrypt: ");
            operation = (Console.ReadLine() ?? "").Trim().ToLower();
            if (operation == "e" || operation == "d") break;
            Console.WriteLine("Invalid choice. Enter 'e' or 'd'.");
        }

        // 4) Choose overwrite or separate file
        bool overwrite = false;
        while (true)
        {
            Console.Write("Overwrite the original file? (y/n): ");
            string choice = (Console.ReadLine() ?? "").Trim().ToLower();
            if (choice == "y") { overwrite = true; break; }
            if (choice == "n") { overwrite = false; break; }
            Console.WriteLine("Enter 'y' or 'n'.");
        }

        // compute output path
        string outputPath = overwrite ? (inputPath + ".tmpxor") : MakeOutputPath(inputPath, operation == "e");

        // Start the progress thread
        processingDone = false;
        cancelRequested = false;
        Thread progressThread = new Thread(ShowProgressLoop)
        {
            IsBackground = true
        };
        progressThread.Start();

        // Start a small watcher thread that detects Enter key to request cancellation.
        Thread cancelWatcher = new Thread(() =>
        {
            // Non-blocking key check loop: user presses Enter to cancel
            while (!processingDone && !cancelRequested)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    if (keyInfo.Key == ConsoleKey.Enter)
                    {
                        cancelRequested = true;
                        break;
                    }
                }
                Thread.Sleep(50);
            }
        })
        { IsBackground = true };
        cancelWatcher.Start();

        bool success = false;
        try
        {
            ProcessFileWithXor(inputPath, outputPath, key);

            // If user requested cancel during processing, do not proceed to replace/finish.
            if (cancelRequested)
            {
                // signal threads and cleanup below
            }
            else
            {
                // Only replace original if not cancelled and overwrite requested
                if (overwrite)
                {
                    try
                    {
                        File.Replace(outputPath, inputPath, null);
                    }
                    catch
                    {
                        // fallback
                        File.Delete(inputPath);
                        File.Move(outputPath, inputPath);
                    }
                    outputPath = inputPath; // final path is original
                }

                // signal success
                success = true;

                // Ensure processedBytes equals totalBytes on finish
                Interlocked.Exchange(ref processedBytes, totalBytes);
            }

            // Signal processing done (so progress thread stops)
            processingDone = true;

            // Give progress thread a moment to print final line
            progressThread.Join(1000);
            cancelWatcher.Join(100);

            // Move to next line (progress used \r)
            Console.WriteLine();

            if (cancelRequested)
            {
                // Rollback: delete the partially written output file if it exists
                try
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("Failed to delete partial output file: " + ex.Message);
                }

                Console.WriteLine("Operation cancelled by user. Partial output removed; original file left unchanged.");
            }
            else
            {
                Console.WriteLine($"{(operation == "e" ? "Encrypted" : "Decrypted")} file saved to: {outputPath}");

                // Show sample bytes after processing
                byte[] afterSample = ReadSampleBytes(outputPath, 16);
                Console.WriteLine("First bytes of output (hex): " + BytesToHex(afterSample));
                Console.WriteLine("If the hex above differs from the first-hex printed earlier, the file changed.");
            }
        }
        catch (Exception ex)
        {
            // make sure progress thread and cancel watcher exit
            processingDone = true;
            cancelWatcher.Join(100);
            progressThread.Join(500);
            Console.WriteLine();
            Console.WriteLine("Error processing file: " + ex.Message);

            // try to remove the partial output file on error
            try
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
            catch { /* ignore */ }
        }
    }

    // Worker: reads input and writes output in chunks, updates processedBytes
    static void ProcessFileWithXor(string inputPath, string outputPath, byte key)
    {
        const int bufferSize = 81920; // 80 KB

        using (FileStream inFs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            totalBytes = inFs.Length;
            Interlocked.Exchange(ref processedBytes, 0);

            byte[] buffer = new byte[bufferSize];
            int read;
            while ((read = inFs.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Check cancellation: if user pressed Enter, stop processing as soon as possible.
                if (cancelRequested)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    buffer[i] ^= key;
                }

                outFs.Write(buffer, 0, read);

                // atomically add the number of bytes processed
                Interlocked.Add(ref processedBytes, read);
            }

            // flush to ensure partial data is written if user cancels (we will delete file later)
            outFs.Flush();
        }
    }

    // Progress loop running on a background thread
    static void ShowProgressLoop()
    {
        const int refreshMs = 150; // how often to update
        DateTime lastUpdate = DateTime.MinValue;
        while (!processingDone)
        {
            // throttle printing to avoid flooding
            if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds >= refreshMs)
            {
                PrintProgressLine();
                lastUpdate = DateTime.UtcNow;
            }
            Thread.Sleep(50);
        }

        // Final print: if cancelled, do not force 100%
        PrintProgressLine(final: !cancelRequested);

        if (cancelRequested)
        {
            Console.Write(" (cancelled)");
        }
    }

    static void PrintProgressLine(bool final = false)
    {
        long total = Interlocked.Read(ref totalBytes);
        long processed = Interlocked.Read(ref processedBytes);

        double percent = 0.0;
        if (total > 0) percent = (processed * 100.0) / total;
        if (percent > 100.0) percent = 100.0;
        if (final) percent = 100.0;

        // Format bytes neatly (KB/MB) for readability
        string processedPretty = PrettyBytes(processed);
        string totalPretty = total > 0 ? PrettyBytes(total) : "(unknown)";

        // Write progress on the same console line using \r
        Console.Write($"\rProgress: {percent:F1}% ({processedPretty} / {totalPretty})");
    }

    // Helper: pretty bytes
    static string PrettyBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("F2") + " MB";
        return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2") + " GB";
    }

    // Create an output filename; if file exists, create unique name
    static string MakeOutputPath(string inputPath, bool encrypt)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? ".";
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string ext = Path.GetExtension(inputPath);

        string suffix = encrypt ? ".enc" : ".dec";
        string candidate = Path.Combine(dir, name + ext + suffix);

        int counter = 1;
        string unique = candidate;
        while (File.Exists(unique))
        {
            string baseName = Path.GetFileNameWithoutExtension(candidate);
            string candidateExt = Path.GetExtension(candidate);
            unique = Path.Combine(dir, $"{baseName}({counter}){candidateExt}");
            counter++;
        }
        return unique;
    }

    static byte[] ReadSampleBytes(string path, int count)
    {
        try
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            int toRead = (int)Math.Min(count, fs.Length);
            byte[] buf = new byte[toRead];
            fs.Read(buf, 0, toRead);
            return buf;
        }
        catch
        {
            return new byte[0];
        }
    }

    static string BytesToHex(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "(empty)";
        StringBuilder sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2")).Append(' ');
        }
        return sb.ToString().Trim();
    }
}
