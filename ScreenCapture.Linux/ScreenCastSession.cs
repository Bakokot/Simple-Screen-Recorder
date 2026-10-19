// Screen Capture: a lightweight screen recorder.
// Copyright (C) 2026 Bakou
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Drawing;
using ScreenCapture.DBus;
using Tmds.DBus;

namespace ScreenCapture;

/// <summary>
/// Permission to capture the screens on Wayland, granted through the ScreenCast portal. The desktop shows its
/// own dialog to pick the screens; the choice is remembered (restore token), so it is only asked once on
/// desktops that support it. Capture lasts as long as the session is open.
/// </summary>
internal sealed class ScreenCastSession : IDisposable
{
    /// <summary>A shared screen: its PipeWire node and its place on the desktop, in logical pixels.</summary>
    /// <param name="Bounds">Null when the desktop does not say where the screen is (single screen).</param>
    public sealed record Stream(uint NodeId, Rectangle? Bounds);

    private const uint Monitor = 1;
    private const uint CursorHidden = 1, CursorEmbedded = 2;
    private const uint PersistUntilRevoked = 2;

    private static string TokenFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCapture", "screencast-token");

    private readonly Portal _portal;
    private readonly ObjectPath _session;

    public IReadOnlyList<Stream> Streams { get; }

    private ScreenCastSession(Portal portal, ObjectPath session, IReadOnlyList<Stream> streams)
    {
        _portal = portal;
        _session = session;
        Streams = streams;
    }

    public static async Task<ScreenCastSession> StartAsync(bool showCursor)
    {
        var portal = await Portal.ConnectAsync();
        try
        {
            var cast = portal.Proxy<IScreenCast>();
            var created = await portal.RequestAsync(token => cast.CreateSessionAsync(new Dictionary<string, object>
            {
                ["handle_token"] = token,
                ["session_handle_token"] = Portal.NewToken(),
            }));
            var session = Portal.Path(created["session_handle"]);

            uint cursorModes;
            try { cursorModes = await cast.GetAsync<uint>("AvailableCursorModes"); }
            catch { cursorModes = CursorHidden; }
            var sources = new Dictionary<string, object>
            {
                ["types"] = Monitor,
                // All screens may be shared at once; the one with the zone is used.
                ["multiple"] = true,
                ["cursor_mode"] = showCursor && (cursorModes & CursorEmbedded) != 0 ? CursorEmbedded : CursorHidden,
                ["persist_mode"] = PersistUntilRevoked,
            };
            if (LoadToken() is { } restore) sources["restore_token"] = restore;
            await portal.RequestAsync(token =>
            {
                sources["handle_token"] = token;
                return cast.SelectSourcesAsync(session, sources);
            });

            var started = await portal.RequestAsync(token =>
                cast.StartAsync(session, "", new Dictionary<string, object> { ["handle_token"] = token }));
            if (started.TryGetValue("restore_token", out var newToken) && newToken is string t) SaveToken(t);

            var streams = new List<Stream>();
            if (started.TryGetValue("streams", out var list))
            {
                foreach (var item in Portal.Items(list))
                {
                    var fields = Portal.Fields(item);
                    var node = Convert.ToUInt32(fields[0]);
                    var props = (IDictionary<string, object>)fields[1]!;
                    Rectangle? bounds = null;
                    if (props.TryGetValue("position", out var pos) && props.TryGetValue("size", out var size))
                    {
                        var p = Portal.Fields(pos);
                        var s = Portal.Fields(size);
                        bounds = new Rectangle(Convert.ToInt32(p[0]), Convert.ToInt32(p[1]), Convert.ToInt32(s[0]), Convert.ToInt32(s[1]));
                    }
                    streams.Add(new Stream(node, bounds));
                }
            }
            if (streams.Count == 0) throw new InvalidOperationException("No screen was shared.");
            return new ScreenCastSession(portal, session, streams);
        }
        catch
        {
            portal.Dispose();
            throw;
        }
    }

    /// <summary>The shared screen that contains <paramref name="region"/>, or null.</summary>
    public Stream? Containing(Rectangle region)
    {
        var placed = Streams.Where(s => s.Bounds is { } b && b.Contains(region)).ToList();
        if (placed.Count > 0) return placed[0];
        // A single screen shared without its position: it is the whole desktop.
        return Streams.Count == 1 && Streams[0].Bounds == null ? Streams[0] : null;
    }

    private static string? LoadToken()
    {
        try { return File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null; }
        catch { return null; }
    }

    private static void SaveToken(string token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
            File.WriteAllText(TokenFile, token);
        }
        catch { /* the dialog will simply be shown again */ }
    }

    public void Dispose()
    {
        try { _portal.Session(_session).CloseAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* already closed */ }
        _portal.Dispose();
    }
}
