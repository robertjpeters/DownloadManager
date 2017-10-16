using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Timers;
using DownloadApp;

namespace DownloadManager
{
    public class Downloader
    {
        // Settable propeties

        /// <summary>
        /// Buffer size.
        /// </summary>
        public int Buffer = 1024;

        /// <summary>
        /// The maximum number of threads to use while downloading.
        /// </summary>
        public int MaximumThreads = 5;

        /// <summary>
        /// How often to fire the update event.
        /// </summary>
        public int UpdateFrequency = 1000;

        /// <summary>
        /// Directory to save the downloaded file in, if not specified the current running directory.
        /// </summary>
        public String Directory;

        /// <summary>
        /// The filename to save the downloaded file as. If not specified, the content-disposition filename is used.
        /// </summary>
        public String SaveAs;

        /// <summary>
        /// Authorization bearer token to be used with the request.
        /// </summary>
        public String Bearer;

        // Events
        public event EventHandler<DownloadProgress> DownloadProgress;
        public event EventHandler<DownloadComplete> DownloadComplete;

        // Important stuff
        protected long ContentLength;
        protected Timer Timer;

        // Progress and its locker
        private readonly object DownloadBytesLocker = new object();
        protected long _DownloadBytes = 0;
        protected long DownloadBytes
        {
            get
            {
                lock (DownloadBytesLocker)
                {
                    return _DownloadBytes;
                }
            }
            set
            {
                lock (DownloadBytesLocker)
                {
                    _DownloadBytes += value;
                }
            }
        }

        private void OnDownloadProgress(DownloadProgress e)
        {
            DownloadProgress?.Invoke(this, e);
        }

        private void OnDownloadComplete(DownloadComplete e)
        {
            DownloadComplete?.Invoke(this, e);
        }

        protected async Task HandleUpdate()
        {
            OnDownloadProgress(new DownloadProgress(DownloadBytes, ContentLength, SaveAs));
        }

        public async Task Download(Uri url)
        {
            // Create an HTTP client, do a get and see if we have a content length on the download
            var httpClient = new HttpClient();

            // Add the bearer if applicable
            if (!String.IsNullOrEmpty(Bearer))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Bearer);
            }

            // Send the request and grab the response
            var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));

            // Look to see if the bytes header exists, meaning we can do a range request
            var parallelDownloadSuported = response.Headers.AcceptRanges.Contains("bytes");
            ContentLength = response.Content.Headers.ContentLength ?? 0;

            // Determine whether a filename save was set, otherwise determine it from the content-disposition header
            SaveAs = SaveAs ?? response.Content.Headers.ContentDisposition?.FileName.Replace("\"", "");
            SaveAs = (String.IsNullOrEmpty(SaveAs) ? url.Segments[url.Segments.Length - 1] : SaveAs);

            // Append the directory to save into if set
            SaveAs = !String.IsNullOrEmpty(Directory) ? Path.Combine(Directory, SaveAs) : SaveAs;

            // Start a timer to report progress
            Timer = new Timer(UpdateFrequency);
            Timer.Elapsed += async (sender, e) => await HandleUpdate();
            Timer.Start();

            // Number of downoad threads to start
            double numberOfParts = parallelDownloadSuported ? MaximumThreads : 1;
            var tasks = new List<Task>();
            var partSize = (long)Math.Ceiling(ContentLength / numberOfParts);

            // Create / truncate the local file
            File.Create(SaveAs).Dispose();

            // Initialize a file to size
            using (var fileStream = new FileStream(SaveAs, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fileStream.SetLength(ContentLength);
            }

            // Create a task for each download part
            for (var i = 0; i < numberOfParts; i++)
            {
                var start = i * partSize + Math.Min(1, i);
                var end = Math.Min((i + 1) * partSize, ContentLength);

                tasks.Add(
                    Task.Run(() => DownloadRange(url, ContentLength, SaveAs, start, end))
                );
            }

            // Wait for them all to complete
            await Task.WhenAll(tasks);

            // Kill the timer
            Timer.Stop();

            // Check for hash
            // TODO: needs to be more generified driver like approach
            try
            {
                String hash = response.Headers.GetValues("content-hash").FirstOrDefault();
                VerifyDropboxHash(hash);
            }
            catch (InvalidOperationException e)
            {
                // Needs fleshed out when this whole hash approach is made better
            }

            // Fire the download complete
            OnDownloadProgress(new DownloadProgress(ContentLength, ContentLength, SaveAs));
            OnDownloadComplete(new DownloadComplete(DownloadBytes, ContentLength, SaveAs));
        }

        private async void DownloadRange(Uri url, long contentLength, string saveAs, long start, long end)
        {
            // Create a new webrequest
            var webRequest = (HttpWebRequest)WebRequest.Create(url);
            webRequest.AddRange(start, end);

            // Add bearer if necessary / specified
            if (!String.IsNullOrEmpty(Bearer))
            {
                webRequest.Headers.Add("Authorization", "Bearer " + Bearer);
            }

            using (HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse())
            {
                byte[] buffer = new byte[Buffer];

                // Create a new file at the correct position in the byte range
                var fileStream = new FileStream(saveAs, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.Position = start;

                using (Stream input = webResponse.GetResponseStream())
                {
                    int size;
                    while ((size = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        DownloadBytes = size;
                        fileStream.Write(buffer, 0, size);
                    }
                }

                fileStream.Close();
            }
        }

        protected void VerifyDropboxHash(String hash)
        {
            string dropboxHash = CalculateDropboxHash();
            if (hash != dropboxHash)
            {
                throw new Exception("Dropbox hash does not match.");
            }
        }

        protected string CalculateDropboxHash()
        {
            var hasher = new DropboxContentHasher();
            byte[] buf = new byte[1024];
            using (var file = File.OpenRead(SaveAs))
            {
                while (true)
                {
                    int n = file.Read(buf, 0, buf.Length);
                    if (n <= 0) break;  // EOF
                    hasher.TransformBlock(buf, 0, n, buf, 0);
                }
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            string hexHash = DropboxContentHasher.ToHex(hasher.Hash);

            return hexHash;
        }
    }

    public class DownloadProgress : EventArgs
    {
        public long Read { get; private set; }
        public long Total { get; private set; }
        public string FilePath { get; private set; }

        public DownloadProgress(long read, long total, string filePath)
        {
            Read = read;
            Total = total;
            FilePath = filePath;
        }
    }

    public class DownloadComplete : EventArgs
    {
        public long Read { get; private set; }
        public long Total { get; private set; }
        public string FilePath { get; private set; }

        public DownloadComplete(long read, long total, string filePath)
        {
            Read = read;
            Total = total;
            FilePath = filePath;
        }
    }
}