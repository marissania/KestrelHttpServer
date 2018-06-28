// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http
{
    public class Http1OutputProducer : IHttpOutputProducer
    {
        private static readonly ReadOnlyMemory<byte> _continueBytes = new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
        private static readonly byte[] _bytesHttpVersion11 = Encoding.ASCII.GetBytes("HTTP/1.1 ");
        private static readonly byte[] _bytesEndHeaders = Encoding.ASCII.GetBytes("\r\n\r\n");
        private static readonly ReadOnlyMemory<byte> _endChunkedResponseBytes = new ReadOnlyMemory<byte>(Encoding.ASCII.GetBytes("0\r\n\r\n"));

        private readonly string _connectionId;
        private readonly ConnectionContext _connectionContext;
        private readonly ITimeoutControl _timeoutControl;
        private readonly IKestrelTrace _log;
        private readonly IBytesWrittenFeature _transportBytesWrittenFeature;
        private readonly TimingPipeWriterFlusher _flusher;

        // This locks access to to all of the below fields
        private readonly object _contextLock = new object();

        private bool _completed = false;
        private bool _aborted;
        private long _unflushedBytes;
        private long _totalBytesCommitted;

        private readonly PipeWriter _pipeWriter;
        public Http1OutputProducer(
            PipeWriter pipeWriter,
            string connectionId,
            ConnectionContext connectionContext,
            IKestrelTrace log,
            ITimeoutControl timeoutControl,
            IBytesWrittenFeature transportBytesWrittenFeature)
        {
            _pipeWriter = pipeWriter;
            _connectionId = connectionId;
            _connectionContext = connectionContext;
            _timeoutControl = timeoutControl;
            _log = log;
            _transportBytesWrittenFeature = transportBytesWrittenFeature;
            _flusher = new TimingPipeWriterFlusher(pipeWriter, timeoutControl, this);
        }

        public Task WriteDataAsync(ReadOnlySpan<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            return WriteAsync(buffer, cancellationToken);
        }

        public Task WriteStreamSuffixAsync()
        {
            return WriteAsync(_endChunkedResponseBytes.Span);
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync(Constants.EmptyData, cancellationToken);
        }

        public void Write<T>(Func<PipeWriter, T, long> callback, T state)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return;
                }

                var buffer = _pipeWriter;
                var bytesCommitted = callback(buffer, state);
                _unflushedBytes += bytesCommitted;
                _totalBytesCommitted += bytesCommitted;
            }
        }

        public Task WriteAsync<T>(Func<PipeWriter, T, long> callback, T state)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return Task.CompletedTask;
                }

                var buffer = _pipeWriter;
                var bytesCommitted = callback(buffer, state);
                _unflushedBytes += bytesCommitted;
                _totalBytesCommitted += bytesCommitted;
            }

            return FlushAsync();
        }

        public void WriteResponseHeaders(int statusCode, string reasonPhrase, HttpResponseHeaders responseHeaders)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return;
                }

                var buffer = _pipeWriter;
                var writer = new CountingBufferWriter<PipeWriter>(buffer);

                writer.Write(_bytesHttpVersion11);
                var statusBytes = ReasonPhrases.ToStatusBytes(statusCode, reasonPhrase);
                writer.Write(statusBytes);
                responseHeaders.CopyTo(ref writer);
                writer.Write(_bytesEndHeaders);

                writer.Commit();

                _unflushedBytes += writer.BytesCommitted;
                _totalBytesCommitted += writer.BytesCommitted;
            }
        }

        public void Dispose()
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return;
                }

                _log.ConnectionDisconnect(_connectionId);
                _completed = true;
                _pipeWriter.Complete();

                var unsentBytes = _totalBytesCommitted - _transportBytesWrittenFeature.TotalBytesWritten;

                if (unsentBytes > 0)
                {
                    // unsentBytes should never be over 64KB in the default configuration.
                    _timeoutControl.StartTimingWrite((int)Math.Min(unsentBytes, int.MaxValue));
                    _pipeWriter.OnReaderCompleted((ex, state) => ((ITimeoutControl)state).StopTimingWrite(), _timeoutControl);
                }
            }
        }

        public void Abort(ConnectionAbortedException error)
        {
            // Abort can be called after Dispose if there's a flush timeout.
            // It's important to still call _lifetimeFeature.Abort() in this case.

            lock (_contextLock)
            {
                if (_aborted)
                {
                    return;
                }

                _aborted = true;
                _connectionContext.Abort(error);

                if (!_completed)
                {
                    _log.ConnectionDisconnect(_connectionId);
                    _completed = true;
                    _pipeWriter.Complete();
                }
            }
        }

        public Task Write100ContinueAsync()
        {
            return WriteAsync(_continueBytes.Span);
        }

        private Task WriteAsync(
            ReadOnlySpan<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            lock (_contextLock)
            {
                if (_completed)
                {
                    return Task.CompletedTask;
                }

                var writer = new CountingBufferWriter<PipeWriter>(_pipeWriter);
                if (buffer.Length > 0)
                {
                    writer.Write(buffer);

                    _unflushedBytes += buffer.Length;
                    _totalBytesCommitted += buffer.Length;
                }
                writer.Commit();

                var bytesWritten = _unflushedBytes;
                _unflushedBytes = 0;

                return _flusher.FlushAsync(bytesWritten, cancellationToken);
            }
        }
    }
}
