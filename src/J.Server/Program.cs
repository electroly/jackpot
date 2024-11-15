using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Web;
using Amazon.S3;
using J.Base;
using J.Core;
using J.Core.Data;
using J.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;

const int BLOCKS_PER_PAGE = 25;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCore();
builder.Services.AddHttpLogging(o => { });
builder.Services.AddTransient<ServerMovieFileReader>();
builder.Services.AddSingleton<IAmazonS3>(services =>
    services.GetRequiredService<AccountSettingsProvider>().CreateAmazonS3Client()
);

var app = builder.Build();
app.UseHttpLogging();

var configuredPort = GetPortNumber();
var configuredSessionPassword =
    Environment.GetEnvironmentVariable("JACKPOT_SESSION_PASSWORD")
    ?? throw new Exception("Session password is required.");
var libraryProvider = app.Services.GetRequiredService<LibraryProvider>();
libraryProvider.Connect();
var accountSettingsProvider = app.Services.GetRequiredService<AccountSettingsProvider>();
var accountSettings = accountSettingsProvider.Current;
var bucket = accountSettings.Bucket;
var password = accountSettings.Password ?? throw new Exception("Encryption key not found.");

bool isVlcInstalled;
try
{
    using var key = Registry.ClassesRoot.OpenSubKey(@"Applications\vlc.exe");
    isVlcInstalled = key is not null;
}
catch
{
    isVlcInstalled = false;
}

var preferences = app.Services.GetRequiredService<Preferences>();
object optionsLock = new();
var optionShuffle = preferences.GetBoolean(Preferences.Key.Shared_UseShuffle);
Filter optionFilter = new(true, []);
Dictionary<ListPageKey, Lazy<List<Page>>> listPages = [];
Dictionary<TagId, Lazy<List<Page>>> tagPages = [];

void RefreshLibrary()
{
    lock (optionsLock)
    {
        var movies = GetFilteredMovies(libraryProvider, optionFilter);

        // List pages
        {
            Dictionary<ListPageKey, Lazy<List<Page>>> dict = [];
            dict[new ListPageKey(ListPageType.Movies, null)] = new(
                () =>
                    SplitMoviesIntoPages(
                        movies,
                        "Movies",
                        optionShuffle,
                        n =>
                            $"/list.html?sessionPassword={configuredSessionPassword}&type={ListPageType.Movies}&pageIndex={n - 1}"
                    )
            );

            foreach (var tagType in libraryProvider.GetTagTypes())
            {
                ListPageKey key = new(ListPageType.TagType, tagType.Id);
                dict[key] = new(
                    () =>
                        GetTagListPage(
                            libraryProvider,
                            tagType,
                            optionShuffle,
                            n =>
                                $"/list.html?sessionPassword={configuredSessionPassword}&type={ListPageType.TagType}&tagTypeId={tagType.Id.Value}&pageIndex={n - 1}"
                        )
                );
            }

            listPages = dict;
        }

        // Individual tag pages
        {
            Dictionary<TagId, Lazy<List<Page>>> dict = [];
            var movieIds = movies.Select(x => x.Id).ToHashSet();
            foreach (var tag in libraryProvider.GetTags())
                dict[tag.Id] = new(
                    () =>
                        SplitMoviesIntoPages(
                            libraryProvider.GetMoviesWithTag(movieIds, tag.Id),
                            tag.Name,
                            optionShuffle,
                            n =>
                                $"/tag.html?sessionPassword={configuredSessionPassword}&tagId={tag.Id.Value}&pageIndex={n - 1}"
                        )
                );
            tagPages = dict;
        }
    }
}

static List<Movie> GetFilteredMovies(LibraryProvider libraryProvider, Filter filter)
{
    var movies = libraryProvider.GetMovies();
    if (filter.Rules.Count == 0)
        return movies;

    var movieTags = libraryProvider.GetMovieTags().ToLookup(x => x.MovieId, x => x.TagId);
    var tagTypes = libraryProvider.GetTags().ToDictionary(x => x.Id, x => x.TagTypeId);
    return movies.Where(IsMovieIncludedInFilter).ToList();

    bool IsMovieIncludedInFilter(Movie movie)
    {
        var thisTagIds = movieTags[movie.Id];
        var thisTagTypes = thisTagIds.Select(x => tagTypes[x]);

        var anyTrue = false;
        var anyFalse = false;
        void Add(bool x)
        {
            if (x)
                anyTrue = true;
            else
                anyFalse = true;
        }

        foreach (var rule in filter.Rules)
        {
            switch (rule.Operator)
            {
                case FilterOperator.IsTagged:
                    Add(thisTagTypes.Contains(rule.Field.TagType!.Value.Id));
                    break;

                case FilterOperator.IsNotTagged:
                    Add(!thisTagTypes.Contains(rule.Field.TagType!.Value.Id));
                    break;

                case FilterOperator.IsTag:
                    foreach (var tag in rule.TagValues!)
                        Add(thisTagIds.Contains(tag.Id));
                    break;

                case FilterOperator.IsNotTag:
                    foreach (var tag in rule.TagValues!)
                        Add(!thisTagIds.Contains(tag.Id));
                    break;

                case FilterOperator.ContainsString:
                    Add(movie.Filename.Contains(rule.StringValue!, StringComparison.InvariantCultureIgnoreCase));
                    break;

                case FilterOperator.DoesNotContainString:
                    Add(!movie.Filename.Contains(rule.StringValue!, StringComparison.InvariantCultureIgnoreCase));
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected operator: {rule.Operator}");
            }
        }

        if (filter.Or)
            return anyTrue; // OR
        else
            return !anyFalse; // AND
    }
}

static List<Page> GetTagListPage(
    LibraryProvider libraryProvider,
    TagType tagType,
    bool shuffle,
    Func<int, string> getUrlForPageNumber
)
{
    var tags = libraryProvider.GetTags(tagType.Id);
    var dict = libraryProvider.GetRandomMoviePerTag(tagType);
    List<Page.Block> blocks = [];
    foreach (var tag in tags)
    {
        if (!dict.TryGetValue(tag.Id, out var movieId))
            continue;

        Page.Block block = new(movieId, tag.Id, tag.Name);
        blocks.Add(block);
    }
    return SplitBlocksIntoPages(blocks, tagType.PluralName, shuffle, getUrlForPageNumber);
}

static List<Page> SplitMoviesIntoPages(
    List<Movie> movies,
    string title,
    bool shuffle,
    Func<int, string> getUrlForPageNumber
)
{
    return SplitBlocksIntoPages(
        (from x in movies select new Page.Block(x.Id, null, x.Filename)).ToList(),
        title,
        shuffle,
        getUrlForPageNumber
    );
}

static List<Page> SplitBlocksIntoPages(
    List<Page.Block> blocks,
    string title,
    bool shuffle,
    Func<int, string> getUrlForPageNumber
)
{
    if (shuffle)
        Shuffle(blocks);
    else
        blocks.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.Ordinal));

    List<Page> pages = new(blocks.Count / BLOCKS_PER_PAGE + 1);
    var pageNumber = 1;
    foreach (var chunk in blocks.Chunk(BLOCKS_PER_PAGE))
    {
        var previousPageUrl = pageNumber > 1 ? getUrlForPageNumber(pageNumber - 1) : "";
        var nextPageUrl = getUrlForPageNumber(pageNumber + 1);
        Page page = new([.. chunk], "", previousPageUrl, nextPageUrl);
        pages.Add(page);
        pageNumber++;
    }

    // Blank out the NextPageUrl for the last page.
    if (pages.Count > 0)
        pages[^1] = pages[^1] with { NextPageUrl = "" };

    for (var i = 0; i < pages.Count; i++)
    {
        pages[i] = pages[i] with { Title = $"{title} ({i + 1}/{pages.Count})" };
    }

    return pages;
}

static void Shuffle<T>(List<T> list)
{
    for (var i = 0; i < list.Count; i++)
    {
        var j = Random.Shared.Next(i, list.Count);
        (list[i], list[j]) = (list[j], list[i]);
    }
}

static int GetPortNumber()
{
    var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (string.IsNullOrEmpty(urls))
    {
        throw new InvalidOperationException("ASPNETCORE_URLS environment variable is not set");
    }

    // Take first URL if multiple are specified
    var url = urls.Split(';').First();

    // Find the last occurrence of : and parse everything after it
    var lastColon = url.LastIndexOf(':');
    if (lastColon == -1)
    {
        throw new InvalidOperationException($"Could not parse port from URL: {url}");
    }

    var portStr = url[(lastColon + 1)..];
    if (!int.TryParse(portStr, out int port))
    {
        throw new InvalidOperationException($"Invalid port number: {portStr}");
    }

    return port;
}

void CheckSessionPassword(string sessionPassword)
{
    if (sessionPassword != configuredSessionPassword)
        throw new Exception("Unrecognized caller.");
}

RefreshLibrary();

// ---

app.MapGet(
    "/movie.m3u8",
    async (
        [FromQuery, Required] string movieId,
        [FromQuery, Required] string sessionPassword,
        HttpResponse response,
        ServerMovieFileReader movieFileReader,
        CancellationToken cancel
    ) =>
    {
        CheckSessionPassword(sessionPassword);
        var m3u8 = libraryProvider.GetM3u8(new(movieId), configuredPort, configuredSessionPassword);
        response.ContentType = "application/vnd.apple.mpegurl";
        await response.StartAsync(cancel);
        await response.Body.WriteAsync(m3u8, cancel);
    }
);

app.MapGet(
    "/movie.ts",
    async (
        [FromQuery, Required] string movieId,
        [FromQuery, Required] int index,
        [FromQuery, Required] string sessionPassword,
        HttpResponse response,
        ServerMovieFileReader movieFileReader,
        CancellationToken cancel
    ) =>
    {
        CheckSessionPassword(sessionPassword);
        await using MemoryStream output = new();
        await ServerPolicy.Policy.ExecuteAsync(
            delegate
            {
                movieFileReader.ReadTs(new(movieId), index, bucket, password, output);
                return Task.CompletedTask;
            }
        );

        output.Position = 0;
        response.ContentType = "video/MP2T";
        await response.StartAsync(cancel);
        await output.CopyToAsync(response.Body, cancel);
    }
);

app.MapGet(
    "/clip.mp4",
    async (
        [FromQuery, Required] string movieId,
        [FromQuery, Required] string sessionPassword,
        HttpResponse response,
        CancellationToken cancel
    ) =>
    {
        CheckSessionPassword(sessionPassword);
        var bytes = libraryProvider.GetMovieClip(new(movieId));

        response.ContentType = "video/mp4";
        await response.StartAsync(cancel);
        await using var output = response.Body;
        await output.WriteAsync(bytes, cancel);
    }
);

app.MapPost(
    "/refresh-library",
    ([FromQuery, Required] string sessionPassword) =>
    {
        CheckSessionPassword(sessionPassword);
        RefreshLibrary();
    }
);

app.MapGet(
    "/list.html",
    (
        [FromQuery, Required] string type,
        [FromQuery] string? tagTypeId,
        [FromQuery, Required] int pageIndex,
        [FromQuery, Required] string sessionPassword,
        HttpResponse response
    ) =>
    {
        CheckSessionPassword(sessionPassword);
        Page page;

        var listPageType = (ListPageType)Enum.Parse(typeof(ListPageType), type);
        ListPageKey key = new(listPageType, tagTypeId is null ? null : new(tagTypeId));
        var lazy = listPages[key];
        if (pageIndex < 0 || pageIndex >= lazy.Value.Count)
            page = new([], "Blank", "", "");
        else
            page = lazy.Value[pageIndex];

        response.ContentType = "text/html";
        return page.ToHtml(configuredSessionPassword);
    }
);

app.MapGet(
    "/tag.html",
    (
        [FromQuery, Required] string tagId,
        [FromQuery, Required] int pageIndex,
        [FromQuery, Required] string sessionPassword,
        HttpResponse response
    ) =>
    {
        CheckSessionPassword(sessionPassword);
        Page page;

        var lazy = tagPages[new(tagId)];
        if (pageIndex < 0 || pageIndex >= lazy.Value.Count)
            page = new([], "", "", "");
        else
            page = lazy.Value[pageIndex];

        response.ContentType = "text/html";
        return page.ToHtml(configuredSessionPassword);
    }
);

app.MapPost(
    "/open-movie",
    ([FromQuery, Required] string movieId, [FromQuery, Required] string sessionPassword, HttpResponse response) =>
    {
        CheckSessionPassword(sessionPassword);
        var movie = libraryProvider.GetMovie(new(movieId));
        var query = HttpUtility.ParseQueryString("");
        query["movieId"] = movie.Id.Value;
        query["sessionPassword"] = configuredSessionPassword;
        var url = $"http://localhost:{configuredPort}/movie.m3u8?{query}";

        var extraArgs = "";
        if (!isVlcInstalled)
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jackpot",
                "vlcrc"
            );

            if (!File.Exists(configPath))
            {
                File.WriteAllText(
                    configPath,
                    """
                    metadata-network-access=0
                    qt-updates-notif=0
                    qt-privacy-ask=0
                    """
                );
            }

            extraArgs = $"--config \"{configPath}\"";
        }

        ProcessStartInfo psi =
            new()
            {
                FileName = isVlcInstalled ? "vlc.exe" : Path.Combine(AppContext.BaseDirectory, "..", "vlc", "vlc.exe"),
                Arguments = $"--no-plugins-scan --high-priority {extraArgs} -- {url}",
                UseShellExecute = isVlcInstalled,
            };
        using var p = Process.Start(psi)!;
        ApplicationSubProcesses.Add(p);
    }
);

app.MapPost(
    "/shuffle",
    ([FromQuery, Required] bool on, [FromQuery, Required] string sessionPassword) =>
    {
        CheckSessionPassword(sessionPassword);
        lock (optionsLock)
        {
            optionShuffle = on;
            RefreshLibrary();
        }
    }
);

app.MapPost(
    "/filter",
    ([FromBody] Filter filter, [FromQuery, Required] string sessionPassword) =>
    {
        CheckSessionPassword(sessionPassword);
        lock (optionsLock)
        {
            optionFilter = filter;
            RefreshLibrary();
        }
    }
);

app.Run();

enum ListPageType
{
    Movies,
    TagType,
}

readonly record struct ListPageKey(ListPageType ListPageType, TagTypeId? TagTypeId);
