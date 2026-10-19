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

using System.Collections;
using System.Runtime.CompilerServices;
using Tmds.DBus;

// Tmds.DBus generates the proxies at run time, so the D-Bus interfaces must be public.
namespace ScreenCapture.DBus
{
    [DBusInterface("org.freedesktop.portal.ScreenCast")]
    public interface IScreenCast : IDBusObject
    {
        Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
        Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
        Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);
        Task<T> GetAsync<T>(string prop);
    }

    [DBusInterface("org.freedesktop.portal.Screenshot")]
    public interface IScreenshot : IDBusObject
    {
        Task<ObjectPath> ScreenshotAsync(string parentWindow, IDictionary<string, object> options);
    }

    [DBusInterface("org.freedesktop.portal.Request")]
    public interface IRequest : IDBusObject
    {
        Task<IDisposable> WatchResponseAsync(Action<(uint response, IDictionary<string, object> results)> handler, Action<Exception>? onError = null);
    }

    [DBusInterface("org.freedesktop.portal.Session")]
    public interface ISession : IDBusObject
    {
        Task CloseAsync();
    }

    [DBusInterface("org.freedesktop.FileManager1")]
    public interface IFileManager1 : IDBusObject
    {
        Task ShowItemsAsync(string[] uris, string startupId);
    }
}

namespace ScreenCapture
{
    using ScreenCapture.DBus;

    /// <summary>A connection to the XDG desktop portal, which lets apps capture the screen on Wayland.</summary>
    internal sealed class Portal : IDisposable
    {
        private const string Service = "org.freedesktop.portal.Desktop";
        private static readonly ObjectPath DesktopPath = new("/org/freedesktop/portal/desktop");
        private static int _tokens;

        private readonly Connection _connection;
        /// <summary>Our unique bus name as it appears in request paths (":1.42" → "1_42").</summary>
        private readonly string _sender;

        private Portal(Connection connection, string sender)
        {
            _connection = connection;
            _sender = sender;
        }

        public static async Task<Portal> ConnectAsync()
        {
            var connection = new Connection(Address.Session);
            try
            {
                var info = await connection.ConnectAsync();
                return new Portal(connection, info.LocalName.TrimStart(':').Replace('.', '_'));
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public T Proxy<T>() where T : IDBusObject => _connection.CreateProxy<T>(Service, DesktopPath);

        public ISession Session(ObjectPath handle) => _connection.CreateProxy<ISession>(Service, handle);

        /// <summary>A token unique to this process, usable in an object path.</summary>
        public static string NewToken() => $"screencapture_{Environment.ProcessId}_{Interlocked.Increment(ref _tokens)}";

        /// <summary>
        /// Calls a portal method that answers later through a Request object (often after a dialog), and returns
        /// the results. <paramref name="call"/> must pass the given token as the "handle_token" option.
        /// </summary>
        public async Task<IDictionary<string, object>> RequestAsync(Func<string, Task<ObjectPath>> call)
        {
            // The answer can come before the call returns: watch the request path before calling.
            var token = NewToken();
            var path = new ObjectPath($"/org/freedesktop/portal/desktop/request/{_sender}/{token}");
            var response = new TaskCompletionSource<(uint, IDictionary<string, object>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = _connection.CreateProxy<IRequest>(Service, path);
            using var watch = await request.WatchResponseAsync(r => response.TrySetResult(r), e => response.TrySetException(e));
            await call(token);
            var (code, results) = await response.Task;
            return code switch
            {
                0 => results,
                1 => throw new OperationCanceledException("Screen sharing was cancelled."),
                _ => throw new InvalidOperationException("The desktop did not allow the screen to be captured."),
            };
        }

        public void Dispose() => _connection.Dispose();

        // ------------------------------------------------------------ reading D-Bus values

        /// <summary>The fields of a D-Bus struct, however Tmds.DBus represents it.</summary>
        public static object?[] Fields(object value) => value switch
        {
            ITuple t => Enumerable.Range(0, t.Length).Select(i => t[i]).ToArray(),
            object?[] a => a,
            _ => throw new InvalidDataException($"Unexpected D-Bus value: {value.GetType()}"),
        };

        public static IEnumerable<object> Items(object value) =>
            value is IEnumerable e ? e.Cast<object>() : throw new InvalidDataException($"Unexpected D-Bus value: {value.GetType()}");

        public static ObjectPath Path(object value) => value switch
        {
            ObjectPath p => p,
            string s => new ObjectPath(s),
            _ => throw new InvalidDataException($"Unexpected D-Bus value: {value.GetType()}"),
        };
    }
}
