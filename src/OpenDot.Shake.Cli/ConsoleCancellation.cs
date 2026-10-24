// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace OpenDot.Shake.Cli;

internal sealed class ConsoleCancellation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private bool _disposed;

    public ConsoleCancellation()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public CancellationToken Token => _source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
        lock (_gate)
        {
            _disposed = true;
            _source.Dispose();
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        lock (_gate)
        {
            if (!_disposed)
            {
                _source.Cancel();
            }
        }
    }
}
