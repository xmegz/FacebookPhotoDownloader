using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Program
{
    private static readonly string SessionDirectory =
        Path.Combine(AppContext.BaseDirectory, "facebook-session");

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        string inputUrl = args[0];

        string outputDirectory = args.Length >= 2
            ? Path.GetFullPath(args[1])
            : Path.Combine(AppContext.BaseDirectory, "downloads");

        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(SessionDirectory);

        using var playwright = await Playwright.CreateAsync();

        await using var context =
            await playwright.Chromium.LaunchPersistentContextAsync(
                SessionDirectory,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = false,

                    ViewportSize = null,

                    Locale = "hu-HU",

                    Args = new[]
            {
                "--start-maximized"
            }
                });

        var page =
            context.Pages.FirstOrDefault()
            ?? await context.NewPageAsync();

        page.SetDefaultTimeout(15_000);

        Console.WriteLine("Facebook Photo Downloader");
        Console.WriteLine("-------------------------");
        Console.WriteLine($"URL:    {inputUrl}");
        Console.WriteLine($"Output: {outputDirectory}");
        Console.WriteLine();

        await OpenPageAsync(page, inputUrl);

        await HandleCookieConsentAsync(page);

        if (await IsLoginRequiredAsync(page))
        {
            Console.WriteLine();
            Console.WriteLine("Facebook bejelentkezés szükséges.");
            Console.WriteLine(
                "Jelentkezz be a megnyitott Chromium ablakban.");

            Console.WriteLine();
            Console.WriteLine(
                "Ha kész vagy, nyomj ENTER-t itt a konzolban.");

            Console.ReadLine();

            await OpenPageAsync(page, inputUrl);

            await HandleCookieConsentAsync(page);
        }

        Console.WriteLine();
        Console.WriteLine("Fotók keresése...");

        var photoLinks =
            await CollectPhotoLinksAsync(page);

        if (photoLinks.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "Nem találtam fotólinkeket.");

            Console.WriteLine(
                "Próbáld közvetlenül a profil /photos oldalát megadni.");

            return 2;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Talált fotólinkek: {photoLinks.Count}");

        Console.WriteLine();

        int downloaded = 0;
        int skipped = 0;
        int failed = 0;

        var downloadedImageIds =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        int index = 0;

        foreach (string photoUrl in photoLinks)
        {
            index++;

            Console.Write(
                $"[{index,4}/{photoLinks.Count}] ");

            try
            {
                var result =
                    await GetLargestPhotoAsync(
                        page,
                        photoUrl);

                if (result is null)
                {
                    Console.WriteLine(
                        "nincs használható nagy kép");

                    failed++;
                    continue;
                }

                string imageId =
                    CreateStableId(
                        NormalizeImageUrlForHash(
                            result.Url));

                if (!downloadedImageIds.Add(imageId))
                {
                    Console.WriteLine(
                        "duplikáció");

                    skipped++;
                    continue;
                }

                string extension =
                    GuessExtension(
                        result.Url);

                string filename =
                    $"{downloaded + 1:D5}_" +
                    $"{result.Width}x{result.Height}_" +
                    $"{imageId[..10]}{extension}";

                string targetPath =
                    Path.Combine(
                        outputDirectory,
                        filename);

                bool success =
                    await DownloadImageAsync(
                        context,
                        result.Url,
                        photoUrl,
                        targetPath);

                if (!success)
                {
                    Console.WriteLine(
                        $"hiba: " +
                        $"{result.Width}x{result.Height}");

                    failed++;
                    continue;
                }

                downloaded++;

                Console.WriteLine(
                    $"{result.Width}x{result.Height} " +
                    $"-> {filename}");
            }
            catch (Exception ex)
            {
                failed++;

                Console.WriteLine(
                    $"hiba: {ex.Message}");
            }

            await Task.Delay(
                Random.Shared.Next(
                    500,
                    1200));
        }

        Console.WriteLine();
        Console.WriteLine("Kész.");
        Console.WriteLine(
            $"Letöltve:    {downloaded}");

        Console.WriteLine(
            $"Duplikáció:  {skipped}");

        Console.WriteLine(
            $"Hiba:        {failed}");

        return 0;
    }

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
            1500);
    }

    private static async Task HandleCookieConsentAsync(
        IPage page)
    {
        string[] possibleTexts =
        {
            "Csak a nélkülözhetetlen cookie-k engedélyezése",
            "Csak a szükséges cookie-k engedélyezése",
            "Csak a nélkülözhetetlen sütik engedélyezése",
            "Csak a szükséges sütik engedélyezése",
            "Only allow essential cookies",
            "Allow essential cookies only",
            "Decline optional cookies"
        };

        foreach (string text in possibleTexts)
        {
            try
            {
                var button =
                    page.GetByRole(
                        AriaRole.Button,
                        new PageGetByRoleOptions
                        {
                            Name = text,
                            Exact = false
                        });

                if (await button.CountAsync() > 0)
                {
                    Console.WriteLine(
                        "Cookie ablak felismerve.");

                    await button
                        .First
                        .ClickAsync();

                    await page.WaitForTimeoutAsync(
                        1200);

                    Console.WriteLine(
                        "Cookie beállítás elfogadva.");

                    return;
                }
            }
            catch
            {
                // következő selector
            }
        }

        /*
         * Facebook néha nem button role-ként
         * jeleníti meg a cookie gombokat.
         */
        try
        {
            var buttons =
                page.Locator(
                    "div[role='button']");

            int count =
                await buttons.CountAsync();

            for (int i = 0; i < count; i++)
            {
                var button =
                    buttons.Nth(i);

                string text =
                    (await button.InnerTextAsync())
                    .Trim();

                if (ContainsEssentialCookieText(text))
                {
                    Console.WriteLine(
                        "Cookie ablak felismerve.");

                    await button.ClickAsync();

                    await page.WaitForTimeoutAsync(
                        1200);

                    Console.WriteLine(
                        "Cookie beállítás elfogadva.");

                    return;
                }
            }
        }
        catch
        {
        }
    }

    private static bool ContainsEssentialCookieText(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string value =
            text.ToLowerInvariant();

        bool cookie =
            value.Contains("cookie")
            || value.Contains("süti");

        bool essential =
            value.Contains("nélkülözhetetlen")
            || value.Contains("szükséges")
            || value.Contains("essential");

        return cookie && essential;
    }

    private static async Task<bool> IsLoginRequiredAsync(
        IPage page)
    {
        string url =
            page.Url;

        if (url.Contains(
                "/login",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var password =
                page.Locator(
                    "input[type='password']");

            if (await password.CountAsync() > 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static async Task<List<string>>
        CollectPhotoLinksAsync(
            IPage page)
    {
        var result =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        int unchangedRounds = 0;
        int previousCount = 0;

        const int maxScrollRounds = 300;

        for (int round = 0;
             round < maxScrollRounds;
             round++)
        {
            await CollectLinksFromCurrentPageAsync(
                page,
                result);

            Console.Write(
                $"\r  görgetés: {round + 1,3} | " +
                $"fotók: {result.Count,5}");

            if (result.Count == previousCount)
                unchangedRounds++;
            else
                unchangedRounds = 0;

            previousCount =
                result.Count;

            if (unchangedRounds >= 8)
                break;

            await page.EvaluateAsync(
                """
                () => {
                    window.scrollTo({
                        top: document.body.scrollHeight,
                        behavior: 'instant'
                    });
                }
                """);

            await page.WaitForTimeoutAsync(
                1400);
        }

        Console.WriteLine();

        await CollectLinksFromCurrentPageAsync(
            page,
            result);

        return result.ToList();
    }

    private static async Task
        CollectLinksFromCurrentPageAsync(
            IPage page,
            HashSet<string> result)
    {
        string[] links =
            await page
                .Locator("a[href]")
                .EvaluateAllAsync<string[]>(
                    """
                    links => links
                        .map(a => a.href)
                        .filter(x => !!x)
                    """);

        foreach (string href in links)
        {
            if (!IsPossiblePhotoLink(href))
                continue;

            result.Add(
                NormalizeFacebookUrl(
                    href));
        }
    }

    private static bool IsPossiblePhotoLink(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return false;
        }

        if (!uri.Host.EndsWith(
                "facebook.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string value =
            uri.PathAndQuery;

        if (value.Contains(
                "photo.php",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Contains(
                "/photos/",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Contains(
                "/photo/",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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

        var builder =
            new UriBuilder(uri)
            {
                Fragment = string.Empty
            };

        return builder
            .Uri
            .ToString();
    }

    private static async Task<ImageCandidate?>
        GetLargestPhotoAsync(
            IPage page,
            string photoUrl)
    {
        await OpenPageAsync(
            page,
            photoUrl);

        /*
         * Facebook fotónézet néha pár másodperc
         * alatt cseréli ki a preview képet
         * a nagyobb változatra.
         */
        var candidates =
            await ReadImageCandidatesAsync(
                page);

        ImageCandidate? best =
            candidates
                .Where(IsFacebookImage)
                .OrderByDescending(
                    x =>
                        (long)x.Width *
                        x.Height)
                .FirstOrDefault();

        if (best is not null &&
            best.Width >= 1000)
        {
            return best;
        }

        await page.WaitForTimeoutAsync(
            1500);

        candidates =
            await ReadImageCandidatesAsync(
                page);

        return candidates
            .Where(IsFacebookImage)
            .OrderByDescending(
                x =>
                    (long)x.Width *
                    x.Height)
            .FirstOrDefault();
    }

    private static async Task<ImageCandidate[]>
        ReadImageCandidatesAsync(
            IPage page)
    {
        return await page
            .Locator("img")
            .EvaluateAllAsync<ImageCandidate[]>(
                """
                imgs => imgs
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
                        x.height >= 400)
                """);
    }

    private static bool IsFacebookImage(
        ImageCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(
                candidate.Url))
        {
            return false;
        }

        if (!Uri.TryCreate(
                candidate.Url,
                UriKind.Absolute,
                out Uri? uri))
        {
            return false;
        }

        string host =
            uri.Host;

        return
            host.Contains(
                "fbcdn.net",
                StringComparison.OrdinalIgnoreCase)
            ||
            host.Contains(
                "facebook.com",
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool>
        DownloadImageAsync(
            IBrowserContext context,
            string imageUrl,
            string referer,
            string outputPath)
    {
        var cookies =
            await context.CookiesAsync(
                new[]
                {
                    imageUrl
                });

        using var handler =
            new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.All
            };

        using var http =
            new HttpClient(handler);

        string cookieHeader =
            string.Join(
                "; ",
                cookies.Select(
                    c =>
                        $"{c.Name}={c.Value}"));

        if (!string.IsNullOrEmpty(
                cookieHeader))
        {
            http.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "Cookie",
                    cookieHeader);
        }

        if (Uri.TryCreate(
                referer,
                UriKind.Absolute,
                out Uri? refererUri))
        {
            http.DefaultRequestHeaders.Referrer =
                refererUri;
        }

        http.DefaultRequestHeaders
            .TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/136.0.0.0 Safari/537.36");

        using HttpResponseMessage response =
            await http.GetAsync(
                imageUrl,
                HttpCompletionOption
                    .ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            Console.Write(
                $"HTTP {(int)response.StatusCode} ");

            return false;
        }

        string? contentType =
            response
                .Content
                .Headers
                .ContentType?
                .MediaType;

        if (contentType is not null &&
            !contentType.StartsWith(
                "image/",
                StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(
                $"nem kép: {contentType} ");

            return false;
        }

        await using Stream source =
            await response
                .Content
                .ReadAsStreamAsync();

        await using FileStream destination =
            new(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

        await source.CopyToAsync(
            destination);

        return true;
    }

    private static string GuessExtension(
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

    private static string
        NormalizeImageUrlForHash(
            string value)
    {
        if (!Uri.TryCreate(
                value,
                UriKind.Absolute,
                out Uri? uri))
        {
            return value;
        }

        /*
         * A query stringben sok ideiglenes
         * Facebook paraméter lehet.
         *
         * A host + path általában stabilabb
         * duplikáció-azonosító.
         */
        return
            uri.Host +
            uri.AbsolutePath;
    }

    private static string CreateStableId(
        string value)
    {
        byte[] bytes =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    value));

        return Convert
            .ToHexString(bytes)
            .ToLowerInvariant();
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "FacebookPhotoDownloader " +
            "<facebook-photos-url> [output]");

        Console.WriteLine();

        Console.WriteLine("Példa:");

        Console.WriteLine(
            "FacebookPhotoDownloader.exe " +
            "\"https://www.facebook.com/PROFIL/photos\" " +
            "\"D:\\Facebook\\PROFIL\"");
    }

    private sealed class ImageCandidate
    {
        public string Url { get; set; } =
            string.Empty;

        public int Width { get; set; }

        public int Height { get; set; }
    }
}