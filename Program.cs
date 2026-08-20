

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

internal static class Program
{
    private static readonly string SessionDirectory =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "FacebookMediaDownloader",
            "ChromiumProfile");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        string inputUrl = args[0];

        string outputRoot =
            args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.Combine(
                    AppContext.BaseDirectory,
                    "downloads");

        string photoDirectory =
            Path.Combine(outputRoot, "photos");

        string videoDirectory =
            Path.Combine(outputRoot, "videos");

        string tempDirectory =
            Path.Combine(outputRoot, "_temp");

        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(photoDirectory);
        Directory.CreateDirectory(videoDirectory);
        Directory.CreateDirectory(tempDirectory);

        Console.WriteLine();
        Console.WriteLine("Facebook Media Downloader");
        Console.WriteLine("=========================");
        Console.WriteLine();
        Console.WriteLine($"URL:    {inputUrl}");
        Console.WriteLine($"Output: {outputRoot}");
        Console.WriteLine();

        if (!await IsCommandAvailableAsync(
                "ffmpeg",
                "-version"))
        {
            Console.WriteLine(
                "HIBA: ffmpeg nincs a PATH-ban.");

            return 2;
        }

        if (!await IsCommandAvailableAsync(
                "ffprobe",
                "-version"))
        {
            Console.WriteLine(
                "HIBA: ffprobe nincs a PATH-ban.");

            return 2;
        }

        using var playwright =
            await Playwright.CreateAsync();

        await using var context =
            await playwright.Chromium
                .LaunchPersistentContextAsync(
                    SessionDirectory,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        Headless = false,

                        ViewportSize = null,

                        Locale = "hu-HU",

                        Args =
                        [
                            "--start-maximized"
                        ]
                    });

        IPage page =
            context.Pages.FirstOrDefault()
            ?? await context.NewPageAsync();

        page.SetDefaultTimeout(15_000);

        await OpenPageAsync(
            page,
            inputUrl);

        if (await IsLoginRequiredAsync(page))
        {
            Console.WriteLine();
            Console.WriteLine(
                "Jelentkezz be a Facebookra.");

            Console.WriteLine(
                "Ha kész vagy, nyomj ENTER-t.");

            Console.ReadLine();

            await OpenPageAsync(
                page,
                inputUrl);
        }

        Console.WriteLine();
        Console.WriteLine(
            "Média linkek keresése...");

        MediaLinks media =
            await CollectMediaLinksAsync(
                page);

        Console.WriteLine();
        Console.WriteLine(
            $"Fotók:  {media.PhotoUrls.Count}");

        Console.WriteLine(
            $"Videók: {media.VideoUrls.Count}");

        if (media.PhotoUrls.Count > 0)
        {
            await DownloadPhotosAsync(
                context,
                page,
                media.PhotoUrls,
                photoDirectory);
        }

        if (media.VideoUrls.Count > 0)
        {
            await DownloadVideosAsync(
                context,
                page,
                media.VideoUrls,
                videoDirectory,
                tempDirectory);
        }

        Console.WriteLine();
        Console.WriteLine("KÉSZ.");

        return 0;
    }

    // =========================================================
    // LINK COLLECTION
    // =========================================================

    private static async Task<MediaLinks>
        CollectMediaLinksAsync(
            IPage page)
    {
        var photos =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        var videos =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        int previousTotal = -1;
        int unchangedRounds = 0;

        for (int round = 0; round < 300; round++)
        {
            await CollectLinksAsync(
                page,
                photos,
                videos);

            int total =
                photos.Count +
                videos.Count;

            Console.Write(
                $"\rScroll {round + 1,3} | " +
                $"fotó {photos.Count,5} | " +
                $"videó {videos.Count,5}");

            if (total == previousTotal)
                unchangedRounds++;
            else
                unchangedRounds = 0;

            previousTotal = total;

            if (unchangedRounds >= 10)
                break;

            await page.EvaluateAsync(
                """
                () => {
                    window.scrollTo(
                        0,
                        document.body.scrollHeight
                    );
                }
                """);

            await page.WaitForTimeoutAsync(
                1400);
        }

        Console.WriteLine();

        await CollectLinksAsync(
            page,
            photos,
            videos);

        return new MediaLinks(
            photos.ToList(),
            videos.ToList());
    }

    private static async Task CollectLinksAsync(
        IPage page,
        HashSet<string> photos,
        HashSet<string> videos)
    {
        string[] urls =
            await page
                .Locator("a[href]")
                .EvaluateAllAsync<string[]>(
                    """
                    links =>
                        links
                            .map(x => x.href)
                            .filter(x => !!x)
                    """);

        foreach (string url in urls)
        {
            if (IsPhotoUrl(url))
            {
                photos.Add(
                    NormalizeFacebookUrl(url));
            }

            if (IsVideoUrl(url))
            {
                videos.Add(
                    NormalizeFacebookUrl(url));
            }
        }
    }

    private static bool IsPhotoUrl(
        string url)
    {
        if (!TryGetFacebookUri(
                url,
                out Uri? uri))
        {
            return false;
        }

        string value =
            uri.PathAndQuery;

        return
            value.Contains(
                "photo.php",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "/photos/",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "/photo/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoUrl(
        string url)
    {
        if (!TryGetFacebookUri(
                url,
                out Uri? uri))
        {
            return false;
        }

        string value =
            uri.PathAndQuery;

        return
            value.Contains(
                "/videos/",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "/watch/",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "watch?v=",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "/reel/",
                StringComparison.OrdinalIgnoreCase)
            ||
            value.Contains(
                "/reels/",
                StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================
    // PHOTOS
    // =========================================================

    private static async Task DownloadPhotosAsync(
        IBrowserContext context,
        IPage page,
        List<string> photoUrls,
        string outputDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("========== FOTÓK ==========");

        int downloaded = 0;

        var seen =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        for (int i = 0;
             i < photoUrls.Count;
             i++)
        {
            Console.Write(
                $"[{i + 1}/{photoUrls.Count}] ");

            string photoPageUrl =
                photoUrls[i];

            try
            {
                ImageCandidate? image =
                    await FindLargestImageAsync(
                        page,
                        photoPageUrl);

                if (image is null)
                {
                    Console.WriteLine(
                        "nincs kép");

                    continue;
                }

                string id =
                    StableId(
                        NormalizeMediaUrl(
                            image.Url));

                if (!seen.Add(id))
                {
                    Console.WriteLine(
                        "duplikáció");

                    continue;
                }

                string filename =
                    $"{downloaded + 1:D5}_" +
                    $"{image.Width}x{image.Height}_" +
                    $"{id[..10]}" +
                    GuessImageExtension(
                        image.Url);

                string target =
                    Path.Combine(
                        outputDirectory,
                        filename);

                bool ok =
                    await DownloadHttpFileAsync(
                        context,
                        image.Url,
                        photoPageUrl,
                        target);

                if (!ok)
                {
                    Console.WriteLine(
                        "letöltési hiba");

                    continue;
                }

                downloaded++;

                Console.WriteLine(
                    $"{image.Width}x{image.Height} -> {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"hiba: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Fotók letöltve: {downloaded}");
    }

    private static async Task<ImageCandidate?>
        FindLargestImageAsync(
            IPage page,
            string url)
    {
        await OpenPageAsync(
            page,
            url);

        await page.WaitForTimeoutAsync(
            1200);

        ImageCandidate[] images =
            await GetImagesAsync(page);

        ImageCandidate? best =
            SelectLargestImage(images);

        if (best is not null &&
            best.Width >= 1200)
        {
            return best;
        }

        await page.WaitForTimeoutAsync(
            1800);

        images =
            await GetImagesAsync(page);

        return SelectLargestImage(
            images);
    }

    private static async Task<ImageCandidate[]>
        GetImagesAsync(
            IPage page)
    {
        return await page
            .Locator("img")
            .EvaluateAllAsync<ImageCandidate[]>(
                """
                imgs =>
                    imgs
                        .map(img => ({
                            url:
                                img.currentSrc ||
                                img.src ||
                                '',

                            width:
                                img.naturalWidth || 0,

                            height:
                                img.naturalHeight || 0
                        }))
                        .filter(x =>
                            x.url &&
                            x.width >= 400 &&
                            x.height >= 400
                        )
                """);
    }

    private static ImageCandidate?
        SelectLargestImage(
            IEnumerable<ImageCandidate> images)
    {
        return images
            .Where(x =>
                IsFacebookMediaUrl(
                    x.Url))
            .OrderByDescending(x =>
                (long)x.Width *
                x.Height)
            .FirstOrDefault();
    }

    // =========================================================
    // VIDEOS
    // =========================================================

    private static async Task DownloadVideosAsync(
        IBrowserContext context,
        IPage page,
        List<string> videoUrls,
        string outputDirectory,
        string tempDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("========== VIDEÓK ==========");

        int downloaded = 0;

        for (int i = 0;
             i < videoUrls.Count;
             i++)
        {
            string videoPageUrl =
                videoUrls[i];

            string videoId =
                GetVideoId(
                    videoPageUrl);

            Console.WriteLine();
            Console.WriteLine(
                $"[{i + 1}/{videoUrls.Count}]");

            Console.WriteLine(
                videoPageUrl);

            string workDirectory =
                Path.Combine(
                    tempDirectory,
                    videoId);

            try
            {
                if (Directory.Exists(
                        workDirectory))
                {
                    Directory.Delete(
                        workDirectory,
                        true);
                }

                Directory.CreateDirectory(
                    workDirectory);

                CapturedVideoSources capture =
                    await CaptureVideoSourcesAsync(
                        page,
                        videoPageUrl);

                Console.WriteLine(
                    $"Elfogott MP4 request: " +
                    $"{capture.MediaUrls.Count}");

                if (capture.MediaUrls.Count == 0)
                {
                    Console.WriteLine(
                        "Nem találtam média requestet.");

                    continue;
                }

                List<MediaAsset> assets =
                    BuildMediaAssets(
                        capture.MediaUrls);

                Console.WriteLine(
                    $"Egyedi asset: {assets.Count}");

                foreach (MediaAsset asset in assets)
                {
                    Console.WriteLine(
                        $"  {asset.Tag}");
                }

                List<DownloadedAsset> downloadedAssets =
                    new();

                int assetNumber = 0;

                foreach (MediaAsset asset in assets)
                {
                    assetNumber++;

                    string tempFile =
                        Path.Combine(
                            workDirectory,
                            $"asset_{assetNumber:D2}.mp4");

                    Console.WriteLine();
                    Console.WriteLine(
                        $"Asset {assetNumber}/{assets.Count}");

                    Console.WriteLine(
                        $"  tag: {asset.Tag}");

                    bool ok =
                        await DownloadFullAssetAsync(
                            context,
                            asset.BaseUrl,
                            videoPageUrl,
                            tempFile);

                    if (!ok)
                    {
                        Console.WriteLine(
                            "  letöltési hiba");

                        continue;
                    }

                    FileInfo info =
                        new(tempFile);

                    Console.WriteLine(
                        $"  méret: " +
                        $"{info.Length / 1024.0 / 1024.0:F2} MB");

                    MediaProbe? probe =
                        await ProbeMediaAsync(
                            tempFile);

                    if (probe is null)
                    {
                        Console.WriteLine(
                            "  ffprobe: hibás asset");

                        SafeDelete(
                            tempFile);

                        continue;
                    }

                    Console.WriteLine(
                        $"  ffprobe: " +
                        $"{DescribeProbe(probe)}");

                    downloadedAssets.Add(
                        new DownloadedAsset(
                            asset,
                            tempFile,
                            probe));
                }

                if (downloadedAssets.Count == 0)
                {
                    Console.WriteLine(
                        "Nem sikerült teljes assetet letölteni.");

                    continue;
                }

                DownloadedAsset? bestVideo =
                    downloadedAssets
                        .Where(x =>
                            x.Probe.HasVideo)
                        .OrderByDescending(x =>
                            (long)x.Probe.Width *
                            x.Probe.Height)
                        .ThenByDescending(x =>
                            x.Probe.Bitrate)
                        .FirstOrDefault();

                if (bestVideo is null)
                {
                    Console.WriteLine(
                        "Nem találtam használható videósávot.");

                    continue;
                }

                DownloadedAsset? bestAudio =
                    downloadedAssets
                        .Where(x =>
                            x.Probe.HasAudio &&
                            !x.Probe.HasVideo)
                        .OrderByDescending(x =>
                            x.Probe.Bitrate)
                        .FirstOrDefault();

                string outputFile =
                    Path.Combine(
                        outputDirectory,
                        $"{i + 1:D5}_{videoId}_" +
                        $"{bestVideo.Probe.Height}p.mp4");

                SafeDelete(
                    outputFile);

                Console.WriteLine();
                Console.WriteLine(
                    $"Legjobb video: " +
                    $"{bestVideo.Probe.Width}x" +
                    $"{bestVideo.Probe.Height}");

                bool finalOk;

                if (bestVideo.Probe.HasAudio)
                {
                    Console.WriteLine(
                        "A videó asset már tartalmaz hangot.");

                    finalOk =
                        await RemuxVideoAsync(
                            bestVideo.FilePath,
                            outputFile);
                }
                else if (bestAudio is not null)
                {
                    Console.WriteLine(
                        $"Audio asset: " +
                        $"{bestAudio.Probe.Bitrate / 1000} kbps");

                    finalOk =
                        await MergeVideoAudioAsync(
                            bestVideo.FilePath,
                            bestAudio.FilePath,
                            outputFile);
                }
                else
                {
                    Console.WriteLine(
                        "FIGYELEM: külön audio assetet " +
                        "nem találtam.");

                    Console.WriteLine(
                        "A videót hang nélkül mentem.");

                    finalOk =
                        await RemuxVideoAsync(
                            bestVideo.FilePath,
                            outputFile);
                }

                if (!finalOk)
                {
                    Console.WriteLine(
                        "FFmpeg feldolgozási hiba.");

                    continue;
                }

                MediaProbe? finalProbe =
                    await ProbeMediaAsync(
                        outputFile);

                if (finalProbe is null)
                {
                    Console.WriteLine(
                        "A végső fájl nem valid.");

                    SafeDelete(
                        outputFile);

                    continue;
                }

                downloaded++;

                Console.WriteLine();
                Console.WriteLine(
                    $"OK -> {Path.GetFileName(outputFile)}");

                Console.WriteLine(
                    $"    {DescribeProbe(finalProbe)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"HIBA: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(
                            workDirectory))
                    {
                        Directory.Delete(
                            workDirectory,
                            true);
                    }
                }
                catch
                {
                }
            }

            await Task.Delay(
                Random.Shared.Next(
                    800,
                    1500));
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Videók letöltve: {downloaded}");
    }

    // =========================================================
    // NETWORK CAPTURE
    // =========================================================

    private static async Task<CapturedVideoSources>
        CaptureVideoSourcesAsync(
            IPage page,
            string videoPageUrl)
    {
        var urls =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        void OnRequest(
            object? sender,
            IRequest request)
        {
            try
            {
                string url =
                    request.Url;

                if (!LooksLikeFacebookVideoAsset(
                        url))
                {
                    return;
                }

                lock (urls)
                {
                    urls.Add(url);
                }
            }
            catch
            {
            }
        }

        page.Request += OnRequest;

        try
        {
            await OpenPageAsync(
                page,
                videoPageUrl);

            await page.WaitForTimeoutAsync(
                1800);

            ILocator videos =
                page.Locator("video");

            if (await videos.CountAsync() > 0)
            {
                try
                {
                    await videos
                        .First
                        .EvaluateAsync(
                            """
                            async video => {
                                video.muted = true;

                                try {
                                    await video.play();
                                }
                                catch {
                                }
                            }
                            """);
                }
                catch
                {
                }
            }

            /*
             * Először hagyjuk indulni.
             */
            await page.WaitForTimeoutAsync(
                5000);

            /*
             * Megpróbálunk néhány pozícióra ugrani.
             * Így a Facebook több representation /
             * byte range requestet indíthat.
             */
            if (await videos.CountAsync() > 0)
            {
                double[] positions =
                [
                    0.10,
                    0.35,
                    0.60,
                    0.85
                ];

                foreach (double position in positions)
                {
                    try
                    {
                        await videos
                            .First
                            .EvaluateAsync(
                                """
                                async (video, pos) => {
                                    if (
                                        !Number.isFinite(
                                            video.duration
                                        ) ||
                                        video.duration <= 1
                                    ) {
                                        return;
                                    }

                                    video.currentTime =
                                        video.duration * pos;

                                    try {
                                        await video.play();
                                    }
                                    catch {
                                    }
                                }
                                """,
                                position);

                        await page.WaitForTimeoutAsync(
                            1800);
                    }
                    catch
                    {
                    }
                }
            }

            await page.WaitForTimeoutAsync(
                2000);
        }
        finally
        {
            page.Request -= OnRequest;
        }

        return new CapturedVideoSources(
            urls.ToList());
    }

    private static bool LooksLikeFacebookVideoAsset(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return false;
        }

        bool facebookCdn =
            uri.Host.Contains(
                "fbcdn.net",
                StringComparison.OrdinalIgnoreCase);

        if (!facebookCdn)
            return false;

        return
            uri.AbsolutePath.Contains(
                ".mp4",
                StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================
    // ASSET PARSING
    // =========================================================

    private static List<MediaAsset>
        BuildMediaAssets(
            IEnumerable<string> capturedUrls)
    {
        var result =
            new Dictionary<string, MediaAsset>(
                StringComparer.OrdinalIgnoreCase);

        foreach (string url in capturedUrls)
        {
            MediaAsset? asset =
                ParseMediaAsset(url);

            if (asset is null)
                continue;

            if (!result.ContainsKey(
                    asset.BaseUrl))
            {
                result[
                    asset.BaseUrl] =
                    asset;
            }
        }

        return result.Values.ToList();
    }

    private static MediaAsset? ParseMediaAsset(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return null;
        }

        Dictionary<string, string> query =
            ParseQueryString(
                uri.Query);

        string tag =
            "unknown";

        if (query.TryGetValue(
                "efg",
                out string? efg))
        {
            string? decoded =
                DecodeEfg(efg);

            if (!string.IsNullOrWhiteSpace(
                    decoded))
            {
                tag =
                    ExtractEncodingTag(
                        decoded)
                    ?? decoded;
            }
        }

        /*
         * Range paramétereket elhagyjuk.
         *
         * A signed Facebook URL többi
         * paramétere megmarad.
         */
        var filtered =
            query
                .Where(x =>
                    !x.Key.Equals(
                        "bytestart",
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    !x.Key.Equals(
                        "byteend",
                        StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                    $"{Uri.EscapeDataString(x.Key)}=" +
                    $"{Uri.EscapeDataString(x.Value)}");

        var builder =
            new UriBuilder(uri)
            {
                Query =
                    string.Join(
                        "&",
                        filtered)
            };

        return new MediaAsset(
            builder.Uri.ToString(),
            tag);
    }

    private static string? DecodeEfg(
        string value)
    {
        try
        {
            string decodedUrl =
                Uri.UnescapeDataString(
                    value);

            byte[] bytes =
                Convert.FromBase64String(
                    decodedUrl);

            return Encoding.UTF8.GetString(
                bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractEncodingTag(
        string json)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(json);

            JsonElement root =
                document.RootElement;

            foreach (string name in
                     new[]
                     {
                         "vencode_tag",
                         "encode_tag",
                         "video_tag"
                     })
            {
                if (root.TryGetProperty(
                        name,
                        out JsonElement element))
                {
                    return element
                        .GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    // =========================================================
    // FULL ASSET DOWNLOAD
    // =========================================================

    private static async Task<bool>
        DownloadFullAssetAsync(
            IBrowserContext context,
            string url,
            string referer,
            string targetFile)
    {
        try
        {
            IReadOnlyList<BrowserContextCookiesResult>
                cookies =
                    await context.CookiesAsync(
                        [url]);

            using var handler =
                new HttpClientHandler
                {
                    AutomaticDecompression =
                        DecompressionMethods.All,

                    AllowAutoRedirect = true
                };

            using var client =
                new HttpClient(handler)
                {
                    Timeout =
                        TimeSpan.FromMinutes(10)
                };

            string cookieHeader =
                string.Join(
                    "; ",
                    cookies.Select(
                        c =>
                            $"{c.Name}={c.Value}"));

            if (!string.IsNullOrWhiteSpace(
                    cookieHeader))
            {
                client.DefaultRequestHeaders
                    .TryAddWithoutValidation(
                        "Cookie",
                        cookieHeader);
            }

            client.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 " +
                    "(Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) " +
                    "Chrome/136.0 Safari/537.36");

            if (Uri.TryCreate(
                    referer,
                    UriKind.Absolute,
                    out Uri? refUri))
            {
                client.DefaultRequestHeaders
                    .Referrer =
                    refUri;
            }

            using HttpRequestMessage request =
                new(
                    HttpMethod.Get,
                    url);

            /*
             * 0-tól a végéig.
             *
             * Ha a CDN range alapú, ezzel
             * teljes assetet kérünk.
             */
            request.Headers
                .TryAddWithoutValidation(
                    "Range",
                    "bytes=0-");

            using HttpResponseMessage response =
                await client.SendAsync(
                    request,
                    HttpCompletionOption
                        .ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine(
                    $"  HTTP " +
                    $"{(int)response.StatusCode} " +
                    $"{response.StatusCode}");

                return false;
            }

            if (response.Content.Headers.ContentLength
                is long length)
            {
                Console.WriteLine(
                    $"  HTTP méret: " +
                    $"{length / 1024.0 / 1024.0:F2} MB");
            }

            if (response.Content.Headers
                    .ContentRange is not null)
            {
                Console.WriteLine(
                    $"  Content-Range: " +
                    $"{response.Content.Headers.ContentRange}");
            }

            await using Stream input =
                await response.Content
                    .ReadAsStreamAsync();

            await using FileStream output =
                new(
                    targetFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

            await input.CopyToAsync(
                output);

            return
                output.Length > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"  HTTP hiba: {ex.Message}");

            return false;
        }
    }

    // =========================================================
    // FFPROBE
    // =========================================================

    private static async Task<MediaProbe?>
        ProbeMediaAsync(
            string file)
    {
        var psi =
            new ProcessStartInfo
            {
                FileName = "ffprobe",

                UseShellExecute = false,

                CreateNoWindow = true,

                RedirectStandardOutput = true,

                RedirectStandardError = true
            };

        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");

        psi.ArgumentList.Add(
            "-show_streams");

        psi.ArgumentList.Add(
            "-show_format");

        psi.ArgumentList.Add(
            "-of");

        psi.ArgumentList.Add(
            "json");

        psi.ArgumentList.Add(
            file);

        try
        {
            using Process? process =
                Process.Start(psi);

            if (process is null)
                return null;

            Task<string> stdout =
                process.StandardOutput
                    .ReadToEndAsync();

            Task<string> stderr =
                process.StandardError
                    .ReadToEndAsync();

            await process.WaitForExitAsync();

            string json =
                await stdout;

            string error =
                await stderr;

            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(
                        error))
                {
                    Console.WriteLine(
                        $"  ffprobe: " +
                        $"{FirstLine(error)}");
                }

                return null;
            }

            using JsonDocument doc =
                JsonDocument.Parse(json);

            bool hasVideo = false;
            bool hasAudio = false;

            int width = 0;
            int height = 0;

            long bitrate = 0;

            if (doc.RootElement.TryGetProperty(
                    "streams",
                    out JsonElement streams))
            {
                foreach (
                    JsonElement stream
                    in streams.EnumerateArray())
                {
                    string? codecType =
                        GetString(
                            stream,
                            "codec_type");

                    if (codecType == "video")
                    {
                        hasVideo = true;

                        width =
                            Math.Max(
                                width,
                                GetInt(
                                    stream,
                                    "width"));

                        height =
                            Math.Max(
                                height,
                                GetInt(
                                    stream,
                                    "height"));

                        bitrate =
                            Math.Max(
                                bitrate,
                                GetLongFromString(
                                    stream,
                                    "bit_rate"));
                    }

                    if (codecType == "audio")
                    {
                        hasAudio = true;

                        bitrate =
                            Math.Max(
                                bitrate,
                                GetLongFromString(
                                    stream,
                                    "bit_rate"));
                    }
                }
            }

            if (doc.RootElement.TryGetProperty(
                    "format",
                    out JsonElement format))
            {
                bitrate =
                    Math.Max(
                        bitrate,
                        GetLongFromString(
                            format,
                            "bit_rate"));
            }

            return new MediaProbe(
                hasVideo,
                hasAudio,
                width,
                height,
                bitrate);
        }
        catch
        {
            return null;
        }
    }

    // =========================================================
    // FFMPEG
    // =========================================================

    private static async Task<bool>
        MergeVideoAudioAsync(
            string videoFile,
            string audioFile,
            string outputFile)
    {
        var psi =
            new ProcessStartInfo
            {
                FileName = "ffmpeg",

                UseShellExecute = false,

                CreateNoWindow = true,

                RedirectStandardOutput = true,

                RedirectStandardError = true
            };

        psi.ArgumentList.Add("-y");

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoFile);

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioFile);

        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");

        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("1:a:0");

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");

        psi.ArgumentList.Add(
            "-movflags");

        psi.ArgumentList.Add(
            "+faststart");

        psi.ArgumentList.Add(
            outputFile);

        return await RunProcessAsync(
            psi);
    }

    private static async Task<bool>
        RemuxVideoAsync(
            string inputFile,
            string outputFile)
    {
        var psi =
            new ProcessStartInfo
            {
                FileName = "ffmpeg",

                UseShellExecute = false,

                CreateNoWindow = true,

                RedirectStandardOutput = true,

                RedirectStandardError = true
            };

        psi.ArgumentList.Add("-y");

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputFile);

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");

        psi.ArgumentList.Add(
            "-movflags");

        psi.ArgumentList.Add(
            "+faststart");

        psi.ArgumentList.Add(
            outputFile);

        return await RunProcessAsync(
            psi);
    }

    private static async Task<bool>
        RunProcessAsync(
            ProcessStartInfo psi)
    {
        using Process? process =
            Process.Start(psi);

        if (process is null)
            return false;

        Task<string> stdout =
            process.StandardOutput
                .ReadToEndAsync();

        Task<string> stderr =
            process.StandardError
                .ReadToEndAsync();

        await process.WaitForExitAsync();

        await stdout;

        string error =
            await stderr;

        if (process.ExitCode != 0)
        {
            Console.WriteLine(
                "FFmpeg hiba:");

            Console.WriteLine(
                error);

            return false;
        }

        return true;
    }

    // =========================================================
    // IMAGE DOWNLOAD
    // =========================================================

    private static async Task<bool>
        DownloadHttpFileAsync(
            IBrowserContext context,
            string url,
            string referer,
            string targetFile)
    {
        var cookies =
            await context.CookiesAsync(
                [url]);

        using var handler =
            new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.All
            };

        using var client =
            new HttpClient(handler);

        string cookieHeader =
            string.Join(
                "; ",
                cookies.Select(
                    c =>
                        $"{c.Name}={c.Value}"));

        if (!string.IsNullOrWhiteSpace(
                cookieHeader))
        {
            client.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "Cookie",
                    cookieHeader);
        }

        client.DefaultRequestHeaders
            .TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0");

        if (Uri.TryCreate(
                referer,
                UriKind.Absolute,
                out Uri? refUri))
        {
            client.DefaultRequestHeaders
                .Referrer =
                refUri;
        }

        using HttpResponseMessage response =
            await client.GetAsync(
                url,
                HttpCompletionOption
                    .ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
            return false;

        await using Stream source =
            await response.Content
                .ReadAsStreamAsync();

        await using FileStream destination =
            File.Create(
                targetFile);

        await source.CopyToAsync(
            destination);

        return true;
    }

    // =========================================================
    // FACEBOOK
    // =========================================================

    private static async Task OpenPageAsync(
        IPage page,
        string url)
    {
        await page.GotoAsync(
            url,
            new PageGotoOptions
            {
                WaitUntil =
                    WaitUntilState.DOMContentLoaded,

                Timeout = 60_000
            });

        await page.WaitForTimeoutAsync(
            1200);
    }

    private static async Task<bool>
        IsLoginRequiredAsync(
            IPage page)
    {
        if (page.Url.Contains(
                "/login",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return
            await page
                .Locator(
                    "input[type='password']")
                .CountAsync()
            > 0;
    }

    // =========================================================
    // HELPERS
    // =========================================================

    private static Dictionary<string, string>
        ParseQueryString(
            string query)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(
                query))
        {
            return result;
        }

        string source =
            query.StartsWith("?")
                ? query[1..]
                : query;

        foreach (string part in
                 source.Split(
                     '&',
                     StringSplitOptions
                         .RemoveEmptyEntries))
        {
            int separator =
                part.IndexOf('=');

            if (separator < 0)
            {
                result[
                    Uri.UnescapeDataString(
                        part)] =
                    string.Empty;

                continue;
            }

            string key =
                Uri.UnescapeDataString(
                    part[..separator]);

            string value =
                Uri.UnescapeDataString(
                    part[(separator + 1)..]);

            result[key] =
                value;
        }

        return result;
    }

    private static bool TryGetFacebookUri(
        string url,
        out Uri? uri)
    {
        uri = null;

        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? parsed))
        {
            return false;
        }

        if (!IsFacebookHost(
                parsed.Host))
        {
            return false;
        }

        uri = parsed;

        return true;
    }

    private static bool IsFacebookHost(
        string host)
    {
        return
            host.Equals(
                "facebook.com",
                StringComparison.OrdinalIgnoreCase)
            ||
            host.EndsWith(
                ".facebook.com",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFacebookMediaUrl(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return false;
        }

        return
            uri.Host.Contains(
                "fbcdn.net",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFacebookUrl(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return url;
        }

        return new UriBuilder(uri)
        {
            Fragment =
                string.Empty
        }
        .Uri
        .ToString();
    }

    private static string NormalizeMediaUrl(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return url;
        }

        return
            uri.Host +
            uri.AbsolutePath;
    }

    private static string StableId(
        string value)
    {
        return Convert
            .ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        value)))
            .ToLowerInvariant();
    }

    private static string GuessImageExtension(
        string url)
    {
        string lower =
            url.ToLowerInvariant();

        if (lower.Contains(".png"))
            return ".png";

        if (lower.Contains(".webp"))
            return ".webp";

        if (lower.Contains(".jpeg"))
            return ".jpeg";

        return ".jpg";
    }

    private static string GetVideoId(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return StableId(url)[..10];
        }

        string[] parts =
            uri.AbsolutePath.Split(
                '/',
                StringSplitOptions
                    .RemoveEmptyEntries);

        for (int i = 0;
             i < parts.Length;
             i++)
        {
            if (parts[i].Equals(
                    "videos",
                    StringComparison.OrdinalIgnoreCase)
                &&
                i + 1 < parts.Length)
            {
                return parts[i + 1];
            }

            if (parts[i].Equals(
                    "reel",
                    StringComparison.OrdinalIgnoreCase)
                &&
                i + 1 < parts.Length)
            {
                return parts[i + 1];
            }
        }

        return StableId(url)[..10];
    }

    private static string DescribeProbe(
        MediaProbe probe)
    {
        var parts =
            new List<string>();

        if (probe.HasVideo)
        {
            parts.Add(
                $"video " +
                $"{probe.Width}x{probe.Height}");
        }

        if (probe.HasAudio)
        {
            parts.Add("audio");
        }

        if (probe.Bitrate > 0)
        {
            parts.Add(
                $"{probe.Bitrate / 1000} kbps");
        }

        return string.Join(
            ", ",
            parts);
    }

    private static string? GetString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out JsonElement value))
        {
            return null;
        }

        return value.ValueKind ==
               JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static int GetInt(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out JsonElement value))
        {
            return 0;
        }

        if (value.TryGetInt32(
                out int result))
        {
            return result;
        }

        return 0;
    }

    private static long GetLongFromString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out JsonElement value))
        {
            return 0;
        }

        if (value.ValueKind ==
            JsonValueKind.Number)
        {
            if (value.TryGetInt64(
                    out long numeric))
            {
                return numeric;
            }
        }

        string? text =
            value.GetString();

        return long.TryParse(
            text,
            out long result)
                ? result
                : 0;
    }

    private static string FirstLine(
        string value)
    {
        return value
            .Split(
                new[]
                {
                    '\r',
                    '\n'
                },
                StringSplitOptions
                    .RemoveEmptyEntries)
            .FirstOrDefault()
            ?? value;
    }

    private static void SafeDelete(
        string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
        }
    }

    private static async Task<bool>
        IsCommandAvailableAsync(
            string command,
            string arguments)
    {
        try
        {
            using Process? process =
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            command,

                        Arguments =
                            arguments,

                        UseShellExecute =
                            false,

                        CreateNoWindow =
                            true,

                        RedirectStandardOutput =
                            true,

                        RedirectStandardError =
                            true
                    });

            if (process is null)
                return false;

            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "FacebookMediaDownloader " +
            "<facebook-url> [output]");

        Console.WriteLine();

        Console.WriteLine(
            "Példa:");

        Console.WriteLine(
            "FacebookMediaDownloader.exe " +
            "\"https://www.facebook.com/PROFIL\" " +
            "\"D:\\Facebook\\PROFIL\"");
    }

    // =========================================================
    // MODELS
    // =========================================================

    private sealed class ImageCandidate
    {
        public string Url { get; set; } =
            string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }
    }

    private sealed record MediaLinks(
        List<string> PhotoUrls,
        List<string> VideoUrls);

    private sealed record CapturedVideoSources(
        List<string> MediaUrls);

    private sealed record MediaAsset(
        string BaseUrl,
        string Tag);

    private sealed record DownloadedAsset(
        MediaAsset Asset,
        string FilePath,
        MediaProbe Probe);

    private sealed record MediaProbe(
        bool HasVideo,
        bool HasAudio,
        int Width,
        int Height,
        long Bitrate);
}