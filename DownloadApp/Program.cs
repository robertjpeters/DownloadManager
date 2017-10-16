using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DownloadManager
{
    class Program
    {
        static void Main(string[] args)
        {
            // What to download
            Uri uri = new Uri("https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/debian-9.2.1-amd64-netinst.iso");

            // Create a new instance
            var download = new Downloader
            {
                Directory = "C:\\Users\\rpeters\\Downloads",
                MaximumThreads = 5
            };

            // Properties

            // Setup our callbacks
            download.DownloadProgress += UpdateProgress;
            download.DownloadComplete += UpdateComplete;

            // Create a task
            var task = download.Download( uri );

            // One liner
            //Task.Run(() => download.Download(
            //    uri,
            //    uri.Segments[uri.Segments.Length - 1]
            //)).Wait();

            // RUN FOREST RUN
            Task.Run(() => task);

            // Just to prove it's async
            Console.WriteLine("OFF WE GO");

            // Paint drying
            task.Wait();

            void UpdateProgress(object sender, DownloadProgress e)
            {
                double percentage = ((double)e.Read / (double)e.Total) * 100;
                Console.WriteLine(percentage);
            }

            void UpdateComplete(object sender, DownloadComplete e)
            {
                // YOLO
                Console.WriteLine("Your jacket is now dry");
                Console.ReadLine();
            }
        }
    }
}
