﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace NGitLab.Impl {
    [DataContract]
    internal class JsonError {
#pragma warning disable 649
        [DataMember(Name = "message")]
        public string Message;
#pragma warning restore 649
    }

    public class HttpRequestor {
        readonly MethodType method; // Default to GET requests
        readonly Api root;
        object _data;

        public HttpRequestor(Api root, MethodType method) {
            this.root = root;
            this.method = method;
        }

        public HttpRequestor With(object data) {
            _data = data;
            return this;
        }

        public T To<T>(string tailApiUrl) {
            var result = default(T);
            string json = null;
            Stream(tailApiUrl, s => {
                var data = new StreamReader(s).ReadToEnd();
                result = JsonConvert.DeserializeObject<T>(data);
                json = data;
            });
            return result;
        }

        public void Stream(string tailApiUrl, Action<Stream> parser) {
            var req = SetupConnection(root.GetApiUrl(tailApiUrl));

            if (HasOutput())
                SubmitData(req);
            else if (method == MethodType.Put)
                req.Headers.Add("Content-Length", "0");

            try {
                using (var response = req.GetResponse()) {
                    using (var stream = response.GetResponseStream()) {
                        parser(stream);
                    }
                }
            }
            catch (WebException wex) {
                if (wex.Response != null)
                    using (var errorResponse = (HttpWebResponse)wex.Response) {
                        using (var reader = new StreamReader(errorResponse.GetResponseStream())) {
                            var jsonString = reader.ReadToEnd();
                            JsonError jsonError;
                            try {
                                jsonError = JsonConvert.DeserializeObject<JsonError>(jsonString);
                            }
                            catch (Exception) {
                                throw new Exception($"The remote server returned an error ({errorResponse.StatusCode}) with an empty response");
                            }
                            throw new Exception($"The remote server returned an error ({errorResponse.StatusCode}): {jsonError.Message}");
                        }
                    }
                throw wex;
            }
        }

        public IEnumerable<T> GetAll<T>(string tailUrl) {
            return new Enumerable<T>(root.ApiToken, root.GetApiUrl(tailUrl));
        }

        void SubmitData(WebRequest request) {
            request.ContentType = "application/json";

            using (var writer = new StreamWriter(request.GetRequestStream())) {
                var data = JsonConvert.SerializeObject(_data, new JsonSerializerSettings {
                    NullValueHandling = NullValueHandling.Ignore
                });
                writer.Write(data);
                writer.Flush();
            }
        }

        bool HasOutput() {
            return method == MethodType.Post || method == MethodType.Put && _data != null;
        }

        WebRequest SetupConnection(Uri url) {
            return SetupConnection(url, method, root.ApiToken);
        }

        static WebRequest SetupConnection(Uri url, MethodType methodType, string privateToken) {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = methodType.ToString().ToUpperInvariant();
            request.Headers.Add("Accept-Encoding", "gzip");
            request.AutomaticDecompression = DecompressionMethods.GZip;
            request.Headers["PRIVATE-TOKEN"] = privateToken;
#if DEBUG
            request.Proxy.Credentials = CredentialCache.DefaultCredentials;

            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
#endif

            return request;
        }

        class Enumerable<T> : IEnumerable<T> {
            readonly string apiToken;
            readonly Uri startUrl;

            public Enumerable(string apiToken, Uri startUrl) {
                this.apiToken = apiToken;
                this.startUrl = startUrl;
            }

            public IEnumerator<T> GetEnumerator() {
                return new Enumerator(apiToken, startUrl);
            }

            IEnumerator IEnumerable.GetEnumerator() {
                return GetEnumerator();
            }

            class Enumerator : IEnumerator<T> {
                readonly string apiToken;
                readonly List<T> buffer = new List<T>();
                Uri nextUrlToLoad;

                public Enumerator(string apiToken, Uri startUrl) {
                    this.apiToken = apiToken;
                    nextUrlToLoad = startUrl;
                }

                public void Dispose() {
                }

                public bool MoveNext() {
                    if (buffer.Count == 0) {
                        if (nextUrlToLoad == null)
                            return false;

                        var request = SetupConnection(nextUrlToLoad, MethodType.Get, apiToken);
                        request.Headers["PRIVATE-TOKEN"] = apiToken;

                        using (var response = request.GetResponseAsync().Result) {
                            // <http://localhost:1080/api/v3/projects?page=2&per_page=0>; rel="next", <http://localhost:1080/api/v3/projects?page=1&per_page=0>; rel="first", <http://localhost:1080/api/v3/projects?page=2&per_page=0>; rel="last"
                            var link = response.Headers["Link"];

                            string[] nextLink = null;
                            if (string.IsNullOrEmpty(link) == false)
                                nextLink = link.Split(',')
                                    .Select(l => l.Split(';'))
                                    .FirstOrDefault(pair => pair[1].Contains("next"));

                            if (nextLink != null)
                                nextUrlToLoad = new Uri(nextLink[0].Trim('<', '>', ' '));
                            else
                                nextUrlToLoad = null;

                            var stream = response.GetResponseStream();
                            var data = new StreamReader(stream).ReadToEnd();
                            buffer.AddRange(JsonConvert.DeserializeObject<T[]>(data));
                        }

                        return buffer.Count > 0;
                    }

                    if (buffer.Count > 0) {
                        buffer.RemoveAt(0);
                        return buffer.Count > 0 ? true : MoveNext();
                    }

                    return false;
                }

                public void Reset() {
                    throw new NotImplementedException();
                }

                public T Current => buffer[0];

                object IEnumerator.Current => Current;
            }
        }
    }
}