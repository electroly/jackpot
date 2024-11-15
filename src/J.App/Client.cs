﻿using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Web;
using J.Base;
using J.Core;
using J.Core.Data;

namespace J.App;

public sealed class Client(IHttpClientFactory httpClientFactory, AccountSettingsProvider accountSettingsProvider)
    : IDisposable
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(typeof(Client).FullName!);

    private readonly object _lock = new();
    private Process? _process;

    public int Port { get; private set; } = -1;
    public string SessionPassword { get; } = Guid.NewGuid().ToString();

    public void Start()
    {
        lock (_lock)
        {
            if (_process is not null)
                throw new InvalidOperationException("The web server is already running.");

            var dir = Path.GetDirectoryName(typeof(Client).Assembly.Location!)!;
            var exe = Path.Combine(dir, "Jackpot.Server.exe");

            ProcessStartInfo psi =
                new()
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };

            Port = FindRandomUnusedPort();
            var bindHost = accountSettingsProvider.Current.EnableLocalM3u8Folder ? "*" : "localhost";
            psi.Environment["ASPNETCORE_URLS"] = $"http://{bindHost}:{Port}";
            psi.Environment["JACKPOT_SESSION_PASSWORD"] = SessionPassword;

            _process = Process.Start(psi)!;
            ApplicationSubProcesses.Add(_process);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_process is not null)
            {
                _process.Kill();
                _process.Dispose();
                _process = null;
            }
        }
    }

    public string GetMovieM3u8Url(Movie movie)
    {
        var query = HttpUtility.ParseQueryString("");
        query["sessionPassword"] = SessionPassword;
        query["movieId"] = movie.Id.Value;
        return $"http://localhost:{Port}/movie.m3u8?{query}";
    }

    public async Task RefreshLibraryAsync(CancellationToken cancel)
    {
        var query = HttpUtility.ParseQueryString("");
        query["sessionPassword"] = SessionPassword;
        await _httpClient
            .PostAsync($"http://localhost:{Port}/refresh-library?{query}", new StringContent(""), cancel)
            .ConfigureAwait(false);
    }

    public async Task SetShuffleAsync(bool shuffle, CancellationToken cancel)
    {
        var query = HttpUtility.ParseQueryString("");
        query["sessionPassword"] = SessionPassword;
        query["on"] = shuffle.ToString();
        await _httpClient
            .PostAsync($"http://localhost:{Port}/shuffle?{query}", new StringContent(""), cancel)
            .ConfigureAwait(false);
    }

    public async Task SetFilterAsync(Filter filter, CancellationToken cancel)
    {
        var query = HttpUtility.ParseQueryString("");
        query["sessionPassword"] = SessionPassword;
        await _httpClient
            .PostAsJsonAsync($"http://localhost:{Port}/filter?{query}", filter, cancel)
            .ConfigureAwait(false);
    }

    private static int FindRandomUnusedPort()
    {
        // Create a TCP/IP socket and bind to a random port assigned by the OS
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // Get the assigned port number
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        if (port == 0)
            throw new Exception("Unable to find a port number.");

        return port;
    }
}
