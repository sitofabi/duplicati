﻿//  Copyright (C) 2017, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Runtime.Remoting.Messaging;
using System.Security.Cryptography.X509Certificates;

namespace Duplicati.Library.Utility
{
    internal static class SystemContextSettings
    {
        private struct SystemSettings
        {
            public string Tempdir;
            public long Buffersize;
        }

        public static IDisposable StartSession(string tempdir = null, long buffersize = 0)
        {
            if (string.IsNullOrWhiteSpace(tempdir))
                tempdir = System.IO.Path.GetTempPath();

            if (buffersize < 1024)
                buffersize = 64 * 1024;

            return CallContextSettings<SystemSettings>.StartContext(new SystemSettings()
            {
                Tempdir = tempdir,
                Buffersize = buffersize
            });
        }

        public static string Tempdir
        {
            get 
            {
                var tf = CallContextSettings<SystemSettings>.Settings.Tempdir;
                if (string.IsNullOrWhiteSpace(tf))
                    tf = System.IO.Path.GetTempPath();
                return tf;
            }
            set
            {
                lock (CallContextSettings<SystemSettings>._lock)
                {
                    var st = CallContextSettings<SystemSettings>.Settings;
                    st.Tempdir = value;
                    CallContextSettings<SystemSettings>.Settings = st;
                }
            }
        }

        public static long Buffersize 
        {
            get
            {
                var bs = CallContextSettings<SystemSettings>.Settings.Buffersize;
                if (bs < 1024)
					bs = 64 * 1024;
                return bs;
			}
        }
	}


    /// <summary>
    /// Class for providing call-context access to http settings
    /// </summary>
    public static class HttpContextSettings
    {
        /// <summary>
        /// Internal struct with properties
        /// </summary>
        private struct HttpSettings
        {
            /// <summary>
            /// Gets or sets the operation timeout.
            /// </summary>
            /// <value>The operation timeout.</value>
            public TimeSpan OperationTimeout;
            /// <summary>
            /// Gets or sets the read write timeout.
            /// </summary>
            /// <value>The read write timeout.</value>
            public TimeSpan ReadWriteTimeout;
            /// <summary>
            /// Gets or sets a value indicating whether http requests are buffered.
            /// </summary>
            /// <value><c>true</c> if buffer requests; otherwise, <c>false</c>.</value>
            public bool BufferRequests;
            /// <summary>
            /// Gets or sets the certificate validator.
            /// </summary>
            /// <value>The certificate validator.</value>
            public SslCertificateValidator CertificateValidator;
        }

        /// <summary>
        /// Starts a new session
        /// </summary>
        /// <returns>The session.</returns>
        /// <param name="operationTimeout">The operation timeout.</param>
        /// <param name="readwriteTimeout">The readwrite timeout.</param>
        /// <param name="bufferRequests">If set to <c>true</c> http requests are buffered.</param>
        public static IDisposable StartSession(TimeSpan operationTimeout = default(TimeSpan), TimeSpan readwriteTimeout = default(TimeSpan), bool bufferRequests = false, bool acceptAnyCertificate = false, string[] allowedCertificates = null)
        {
            // Make sure we always use our own version of the callback
			System.Net.ServicePointManager.ServerCertificateValidationCallback = ServicePointManagerCertificateCallback;

            return CallContextSettings<HttpSettings>.StartContext(new HttpSettings()
            {
                OperationTimeout = operationTimeout,
                ReadWriteTimeout = readwriteTimeout,
                BufferRequests = bufferRequests,
                CertificateValidator = acceptAnyCertificate || (allowedCertificates != null)
                    ? new SslCertificateValidator(acceptAnyCertificate, allowedCertificates)
                    : null
            });
        }

        /// <summary>
        /// The callback used to defer the call context, such that each scope can have its own callback
        /// </summary>
        /// <returns><c>true</c>, if point manager certificate callback was serviced, <c>false</c> otherwise.</returns>
        /// <param name="sender">The sender of the validation.</param>
        /// <param name="certificate">The certificate to validate.</param>
        /// <param name="chain">The certificate chain.</param>
        /// <param name="sslPolicyErrors">Errors discovered.</param>
        private static bool ServicePointManagerCertificateCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // If we have a custom SSL validator, invoke it
            if (HttpContextSettings.CertificateValidator != null)
                return CertificateValidator.ValidateServerCertficate(sender, certificate, chain, sslPolicyErrors);

			// Default is to only approve certificates without errors
			var result = sslPolicyErrors == SslPolicyErrors.None;

            // Hack: If we have no validator, see if the context is all messed up
            // This is not the right way, but ServicePointManager is not designed right for this
            var any = false;
            foreach (var v in CallContextSettings<HttpSettings>.GetAllInstances())
                if (v.CertificateValidator != null)
                {
					var t = v.CertificateValidator.ValidateServerCertficate(sender, certificate, chain, sslPolicyErrors);
                    
                    // First instance overrides framework result
                    if (!any)
                        result = t;

                    // If there are more, we see if anyone will accept it
                    else
                        result |= t;
                
                    any = true;
                }

            return result;
        }

        /// <summary>
        /// Gets the operation timeout.
        /// </summary>
        /// <value>The operation timeout.</value>
        public static TimeSpan OperationTimeout => CallContextSettings<HttpSettings>.Settings.OperationTimeout;
        /// <summary>
        /// Gets the read-write timeout.
        /// </summary>
        /// <value>The read write timeout.</value>
		public static TimeSpan ReadWriteTimeout => CallContextSettings<HttpSettings>.Settings.ReadWriteTimeout;
        /// <summary>
        /// Gets a value indicating whether https requests are buffered.
        /// </summary>
        /// <value><c>true</c> if buffer requests; otherwise, <c>false</c>.</value>
		public static bool BufferRequests => CallContextSettings<HttpSettings>.Settings.BufferRequests;
        /// <summary>
        /// Gets or sets the certificate validator.
        /// </summary>
        /// <value>The certificate validator.</value>
        public static SslCertificateValidator CertificateValidator => CallContextSettings<HttpSettings>.Settings.CertificateValidator;

	}

    /// <summary>
    /// Help class for providing settings in the current call context
    /// </summary>
    public static class CallContextSettings<T>
    {
        /// <summary>
        /// Shared key for storing a value in the call context
        /// </summary>
        private const string SETTINGS_KEY_NAME = "duplicati-session-settings";

        /// <summary>
        /// The settings that are stored in the call context, which are not serializable
        /// </summary>
        private static Dictionary<string, T> _setttings = new Dictionary<string, T>();

        /// <summary>
        /// Lock for protecting the dictionary
        /// </summary>
        public static readonly object _lock = new object();

        /// <summary>
        /// Gets all instances currently registered
        /// </summary>
        internal static T[] GetAllInstances()
        {
            lock (_lock)
                return _setttings.Values.ToArray();
        }

        /// <summary>
        /// Gets or sets the values in the current call context
        /// </summary>
        /// <value>The settings.</value>
        public static T Settings
        {
            get
            {
                T res;
                var key = ContextID;
                if (string.IsNullOrWhiteSpace(key))
                    return default(T);

                if (!_setttings.TryGetValue(key, out res))
                    lock (_lock)
                        if (!_setttings.TryGetValue(key, out res))
                            return default(T);

                return res;
            }
            set
            {
				var key = ContextID;
                if (!string.IsNullOrWhiteSpace(key))
                    lock (_lock)
                        _setttings[key] = value;
            }
        }

        /// <summary>
        /// Help class for providing a way to release resources associated with a call context
        /// </summary>
        private class ContextGuard : IDisposable
        {
            /// <summary>
            /// Randomly generated identifier
            /// </summary>
            private readonly string ID = Guid.NewGuid().ToString();

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/> class,
            /// and sets up the call context
            /// </summary>
            public ContextGuard()
            {
				CallContext.LogicalSetData(SETTINGS_KEY_NAME, ID);
			}

            /// <summary>
            /// Releases all resource used by the
            /// <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/> object.
            /// </summary>
            /// <remarks>Call <see cref="Dispose"/> when you are finished using the
            /// <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/>. The <see cref="Dispose"/>
            /// method leaves the <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/> in an
            /// unusable state. After calling <see cref="Dispose"/>, you must release all references to the
            /// <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/> so the garbage collector
            /// can reclaim the memory that the
            /// <see cref="T:Duplicati.Library.Utility.CallContextSettings`1.ContextGuard"/> was occupying.</remarks>
            public void Dispose()
            {
                lock (_lock)
                {
                    // Release the resources, if any
                    _setttings.Remove(ID);
                }

                CallContext.LogicalSetData(SETTINGS_KEY_NAME, null);
            }
        }

        /// <summary>
        /// Starts the context wit a default value.
        /// </summary>
        /// <returns>The disposable handle for the context.</returns>
        /// <param name="initial">The initial value.</param>
        public static IDisposable StartContext(T initial = default(T))
        {
            var res = new ContextGuard();
            Settings = initial;
            return res;
		}

        /// <summary>
        /// Gets the context ID for this call.
        /// </summary>
        /// <value>The context identifier.</value>
        private static string ContextID
        {
            get
            {
                return CallContext.LogicalGetData(SETTINGS_KEY_NAME) as string;
            }
        }

    }
}
