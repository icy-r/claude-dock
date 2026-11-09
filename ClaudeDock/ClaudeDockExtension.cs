using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace ClaudeDock;

[Guid("F302D909-83E8-49CD-B8C1-FACF8C60D4BF")]
public sealed partial class ClaudeDockExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _disposed;
    private readonly ClaudeDockCommandsProvider _provider = new();

    public ClaudeDockExtension(ManualResetEvent disposed) => _disposed = disposed;

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose() => _disposed.Set();
}
