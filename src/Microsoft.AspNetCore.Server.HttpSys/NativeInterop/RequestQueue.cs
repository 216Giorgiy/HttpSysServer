﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal class RequestQueue
    {
        private static readonly int BindingInfoSize =
            Marshal.SizeOf<HttpApi.HTTP_BINDING_INFO>();

        private readonly bool _attachToExistingQueue;
        private readonly UrlGroup _urlGroup;
        private readonly ILogger _logger;
        private bool _disposed;

        // Open existing queue
        internal RequestQueue(UrlGroup urlGroup, string queueName, bool attachToExistingQueue, ILogger logger)
        {
            _attachToExistingQueue = attachToExistingQueue;
            _urlGroup = urlGroup;
            _logger = logger;

            var flags = _attachToExistingQueue ? HttpApi.HTTP_CREATE_REQUEST_QUEUE_FLAG.OpenExisting : HttpApi.HTTP_CREATE_REQUEST_QUEUE_FLAG.None;

            HttpRequestQueueV2Handle requestQueueHandle = null;
            var statusCode = HttpApi.HttpCreateRequestQueue(
                    HttpApi.Version, queueName, null, flags, out requestQueueHandle);

            if (attachToExistingQueue && statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_FILE_NOT_FOUND)
            {
                throw new HttpSysException((int)statusCode, $"Failed to attach to the given request queue '{queueName}', the queue could not be found.");
            }
            else if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS)
            {
                throw new HttpSysException((int)statusCode);
            }

            // Disabling callbacks when IO operation completes synchronously (returns ErrorCodes.ERROR_SUCCESS)
            if (HttpSysListener.SkipIOCPCallbackOnSuccess &&
                !UnsafeNclNativeMethods.SetFileCompletionNotificationModes(
                    requestQueueHandle,
                    UnsafeNclNativeMethods.FileCompletionNotificationModes.SkipCompletionPortOnSuccess |
                    UnsafeNclNativeMethods.FileCompletionNotificationModes.SkipSetEventOnHandle))
            {
                requestQueueHandle.Dispose();
                throw new HttpSysException(Marshal.GetLastWin32Error());
            }

            Handle = requestQueueHandle;
            BoundHandle = ThreadPoolBoundHandle.BindHandle(Handle);
        }

        internal SafeHandle Handle { get; }
        internal ThreadPoolBoundHandle BoundHandle { get; }

        private static void DisableIOCPCallbacks(HttpRequestQueueV2Handle handle)
        {
        }

        internal unsafe void AttachToUrlGroup()
        {
            CheckDisposed();

            if (_attachToExistingQueue)
            {
                return;
            }

            // Set the association between request queue and url group. After this, requests for registered urls will
            // get delivered to this request queue.

            var info = new HttpApi.HTTP_BINDING_INFO();
            info.Flags = HttpApi.HTTP_FLAGS.HTTP_PROPERTY_FLAG_PRESENT;
            info.RequestQueueHandle = Handle.DangerousGetHandle();

            var infoptr = new IntPtr(&info);

            _urlGroup.SetProperty(HttpApi.HTTP_SERVER_PROPERTY.HttpServerBindingProperty,
                infoptr, (uint)BindingInfoSize);
        }

        internal unsafe void DetachFromUrlGroup()
        {
            CheckDisposed();

            if (_attachToExistingQueue)
            {
                return;
            }

            // Break the association between request queue and url group. After this, requests for registered urls
            // will get 503s.
            // Note that this method may be called multiple times (Stop() and then Abort()). This
            // is fine since http.sys allows to set HttpServerBindingProperty multiple times for valid
            // Url groups.

            var info = new HttpApi.HTTP_BINDING_INFO();
            info.Flags = HttpApi.HTTP_FLAGS.NONE;
            info.RequestQueueHandle = IntPtr.Zero;

            var infoptr = new IntPtr(&info);

            _urlGroup.SetProperty(HttpApi.HTTP_SERVER_PROPERTY.HttpServerBindingProperty,
                infoptr, (uint)BindingInfoSize, throwOnError: false);
        }

        // The listener must be active for this to work.
        internal unsafe void SetLengthLimit(long length)
        {
            CheckDisposed();

            var result = HttpApi.HttpSetRequestQueueProperty(Handle,
                HttpApi.HTTP_SERVER_PROPERTY.HttpServerQueueLengthProperty,
                new IntPtr((void*)&length), (uint)Marshal.SizeOf<long>(), 0, IntPtr.Zero);

            if (result != 0)
            {
                throw new HttpSysException((int)result);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            BoundHandle.Dispose();
            Handle.Dispose();
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
        }
    }
}
