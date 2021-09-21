using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Flurl;
using HtmlAgilityPack;
using PlaywrightSharp;

const string bcUrl = @"https://www.britishcycling.org.uk/";
const string clubKitDirectoryUrl = @"https://www.britishcycling.org.uk/club_kit_directory";
const string username = "";
const string password = "";
string outputDir = Path.Combine(Path.GetTempPath(), "kit");

HttpClient httpClient = new HttpClient();

await GetKitInfoFromKitDirectory();

Console.WriteLine("Done");

async Task GetKitInfoFromKitDirectory()
{
    var results = new List<ClubKitInfo>();

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Firefox.LaunchAsync(new LaunchOptions { Headless = false });
    var page = await browser.NewPageAsync();
    await page.GoToAsync(clubKitDirectoryUrl);

    // Login
    await page.FillAsync("#username2", username);
    await page.FillAsync("#password2", password);
    await page.ClickAsync("#login_button");

    // Capture cookies
    var cookies = await page.Context.GetCookiesAsync(bcUrl);

    await page.SelectOptionAsync("[name=club_kits_table_length]", "100");

    int currentPage = 0;
    int maxPage = 5;

    do
    {
        await page.WaitForResponseAsync(resp => resp.Url.Contains("zuvvi")); // Thumbnail requests hit /zuvvi/ paths

        int loadedPage = await GetCurrentPageNumber(page);

        // Sometimes page load takes a while - loop
        while (loadedPage == currentPage)
        {
            Console.WriteLine("Looping for page to load");
            loadedPage = await GetCurrentPageNumber(page);
        }

        maxPage = await GetMaxPageNumberShowing(page);
        currentPage = loadedPage;

        // Grab the table, and parse out the kit URLs
        var tableHtml = await page.GetInnerHtmlAsync("#club_kits_table");
        var document = new HtmlDocument();
        document.LoadHtml(tableHtml);
        var kitForPage = document.DocumentNode.SelectNodes("//tbody/tr").Select(row =>
        {
            var columns = row.SelectNodes(".//td").ToList();
            var name = Url.Decode(columns[0].InnerText, true).Trim();
            var kit = columns[4].SelectNodes(".//a").Select(a => a.GetAttributeValue("data-href", null)).Select(stub => Url.Parse(Url.Combine(bcUrl, stub)));
            var kitInfo = new ClubKitInfo { ClubName = name, KitImageUrls = kit.ToList() };
            results.Add(kitInfo);
            return kitInfo;
        });

        // Download the kit
        var downloadTasks = kitForPage.DistinctBy(d => d.ClubName).Select(kit => DownloadKitForClub(kit, cookies)).ToList();
        await Task.WhenAll(downloadTasks);
        Console.WriteLine($"Downloaded {downloadTasks.Count} kit photos on page {currentPage}");
        await page.ClickAsync("#club_kits_table_next");
    } while (currentPage != maxPage);

}

async Task<int> GetMaxPageNumberShowing(IPage page)
{
    var buttons = await GetButtonsAsync(page);
    return await buttons.ToAsyncEnumerable().MaxAwaitAsync(async b =>
    {
        var text = await b.GetInnerTextAsync();
        return int.Parse(text);
    });
}

async Task<int> GetCurrentPageNumber(IPage page)
{
    var buttons = await GetButtonsAsync(page);
    return await buttons.ToAsyncEnumerable().WhereAwait(async b =>
    {
        var @class = await b.GetAttributeAsync("class");
        return @class.Contains("button--active");
    }).SelectAwait(async b => int.Parse(await b.GetInnerTextAsync())).SingleAsync();
}

async Task<IEnumerable<IElementHandle>> GetButtonsAsync(IPage page) => await page.QuerySelectorAllAsync("//ul[@class ='pagination']//a");

async Task DownloadKitForClub(ClubKitInfo info, IEnumerable<NetworkCookie> cookies)
{
    // Remove invalid chars from club name to use as the output directory
    var clubDir = string.Concat(info.ClubName.Split(Path.GetInvalidFileNameChars()));

    var downloadDir = Path.Combine(outputDir, clubDir);
    Directory.CreateDirectory(downloadDir);

    for (int i = 0; i < info.KitImageUrls.Count; i++)
    {
        var url = info.KitImageUrls[i];
        var request = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = new Version(2, 0)
        };

        request.Headers.Add("Cookie", cookies.Select(c => $"{c.Name}={c.Value};"));
        using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            var outputFileName = response.Content.Headers.ContentType.MediaType switch
            {
                "image/jpeg" => $"{i}.jpg",
                "image/png" => $"{i}.png",
                "application/pdf" => $"{i}.pdf",
                "image/x-ms-bmp" => $"{i}.bmp",
                "image/gif" => $"{i}.gif",
                "application/octet-stream" => $"{i}.webp",
                _ => throw new Exception($"Unknown content type: {response.Content.Headers.ContentType.MediaType}")
            };

            using (var stream = File.OpenWrite(Path.Combine(downloadDir, outputFileName)))
            {
                await response.Content.CopyToAsync(stream);
                await stream.FlushAsync();
            }
        }
    }
};

public record ClubKitInfo
{
    public string ClubName { get; init; }
    public IReadOnlyList<Url> KitImageUrls { get; init; }
}