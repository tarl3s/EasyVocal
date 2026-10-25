using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EasyVocal.Ai.Tests.Helpers;

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 for the download tests (no HttpListener: it needs URL reservations on
/// Windows). Each route answers with a status, optional bytes, an optional length (null = none: read until close)
/// and an optional pause between 16 KB pieces.
/// </summary>
public sealed class LocalHttp : IDisposable
{
    public sealed record Route(int Status, byte[] Body, bool SendLength = true, int PauseMs = 0, long? FakeLength = null);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Dictionary<string, Route> routes = new();
    private readonly CancellationTokenSource stop = new();
    private int requests;

    public LocalHttp()
    {
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Requests => Volatile.Read(ref requests);

    public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";

    public void Map(string path, Route r)
    {
        lock (routes) routes[path] = r;
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using (c)
        {
            try
            {
                var s = c.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await s.ReadAsync(one, stop.Token) <= 0) return;
                    head.Append((char)one[0]);
                }
                Interlocked.Increment(ref requests);
                var path = head.ToString().Split(' ')[1];
                Route? r;
                lock (routes) routes.TryGetValue(path, out r);
                r ??= new Route(404, Encoding.ASCII.GetBytes("not found"));
                var h = new StringBuilder($"HTTP/1.1 {r.Status} {(r.Status == 200 ? "OK" : "Error")}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\n");
                if (r.SendLength) h.Append($"Content-Length: {r.FakeLength ?? r.Body.Length}\r\n");
                h.Append("\r\n");
                var hb = Encoding.ASCII.GetBytes(h.ToString());
                await s.WriteAsync(hb, stop.Token);
                for (int at = 0; at < r.Body.Length; at += 16384)
                {
                    await s.WriteAsync(r.Body.AsMemory(at, Math.Min(16384, r.Body.Length - at)), stop.Token);
                    if (r.PauseMs > 0) await Task.Delay(r.PauseMs, stop.Token);
                }
                await s.FlushAsync(stop.Token);
            }
            catch (Exception) { /* the client went away */ }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
