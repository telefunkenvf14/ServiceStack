﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using ServiceStack.Logging;
using ServiceStack.Serialization;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack
{
    public class JsonHttpClient : IServiceClient
    {
        public static ILog log = LogManager.GetLogger(typeof (JsonHttpClient));

        public static HttpMessageHandler GlobalHttpMessageHandler { get; set; }
        public HttpMessageHandler HttpMessageHandler { get; set; }

        public ResultsFilterDelegate ResultsFilter { get; set; }
        public ResultsFilterHttpResponseDelegate ResultsFilterResponse { get; set; }

        public const string DefaultHttpMethod = "POST";
        public static string DefaultUserAgent = "ServiceStack .NET HttpClient " + Env.ServiceStackVersion;

        public string BaseUri { get; set; }

        public string Format = "json";
        public string ContentType = MimeTypes.Json;

        public string SyncReplyBaseUri { get; set; }

        public string AsyncOneWayBaseUri { get; set; }

        public string UserName { get; set; }
        public string Password { get; set; }
        public bool AlwaysSendBasicAuthHeader { get; set; }

        /// <summary>
        /// Gets the collection of headers to be added to outgoing requests.
        /// </summary>
        public INameValueCollection Headers { get; private set; }

        public void SetBaseUri(string baseUri)
        {
            this.BaseUri = baseUri;
            this.SyncReplyBaseUri = baseUri.WithTrailingSlash() + Format + "/reply/";
            this.AsyncOneWayBaseUri = baseUri.WithTrailingSlash() + Format + "/oneway/";
        }

        public JsonHttpClient(string baseUri)
        {
            BaseUri = baseUri;
            this.Headers = PclExportClient.Instance.NewNameValueCollection();
        }

        public void SetCredentials(string userName, string password)
        {
            this.UserName = userName;
            this.Password = password;
        }

        public virtual string GetBaseUrl(string relativeOrAbsoluteUrl)
        {
            return relativeOrAbsoluteUrl.StartsWith("http:")
                || relativeOrAbsoluteUrl.StartsWith("https:")
                     ? relativeOrAbsoluteUrl
                     : this.BaseUri.CombineWith(relativeOrAbsoluteUrl);
        }

        public HttpClient GetHttpClient()
        {
            var msgHandler = HttpMessageHandler ?? GlobalHttpMessageHandler;
            return msgHandler != null
                ? new HttpClient(msgHandler)
                : new HttpClient();
        }

        public Task<TResponse> SendAsync<TResponse>(string httpMethod, string absoluteUrl, object request)
        {
            if (ResultsFilter != null)
            {
                var response = ResultsFilter(typeof(TResponse), httpMethod, absoluteUrl, request);
                if (response is TResponse)
                {
                    var tcs = new TaskCompletionSource<TResponse>();
                    tcs.SetResult((TResponse)response);
                    return tcs.Task;
                }
            }

            var client = GetHttpClient();

            if (AlwaysSendBasicAuthHeader)
                AddBasicAuth(client);

            var httpReq = new HttpRequestMessage(new HttpMethod(httpMethod), absoluteUrl);

            if (httpMethod.HasRequestBody() && request != null)
            {
                foreach (var name in Headers.AllKeys)
                {
                    httpReq.Headers.Add(name, Headers[name]);
                }
                using (__requestAccess())
                {
                    httpReq.Content = new StringContent(request.ToJson(), Encoding.UTF8, ContentType);
                }
            }

            httpReq.Headers.Add(HttpHeaders.Accept, ContentType);

            var sendAsyncTask = client.SendAsync(httpReq);

            ApplyWebRequestFilters(httpReq);

            if (typeof(TResponse) == typeof(HttpResponseMessage))
            {
                return (Task<TResponse>)(object)sendAsyncTask;
            }

            return sendAsyncTask
                .ContinueWith(responseTask =>
                {
                    var httpRes = responseTask.Result;
                    ApplyWebResponseFilters(httpRes);

                    if (typeof(TResponse) == typeof(byte[]))
                    {
                        return httpRes.Content.ReadAsByteArrayAsync().ContinueWith(task =>
                        {
                            ThrowIfError<TResponse>(task, httpRes, request, absoluteUrl, task.Result);

                            var response = (TResponse)(object)task.Result;

                            if (ResultsFilterResponse != null)
                                ResultsFilterResponse(httpRes, response, httpMethod, absoluteUrl, request);

                            return response;
                        });
                    }
                    if (typeof(TResponse) == typeof(Stream))
                    {
                        return httpRes.Content.ReadAsStreamAsync().ContinueWith(task =>
                        {
                            ThrowIfError<TResponse>(task, httpRes, request, absoluteUrl, task.Result);

                            var response = (TResponse)(object)task.Result;

                            if (ResultsFilterResponse != null)
                                ResultsFilterResponse(httpRes, response, httpMethod, absoluteUrl, request);

                            return response;
                        });
                    }

                    return httpRes.Content.ReadAsStringAsync().ContinueWith(task =>
                    {
                        ThrowIfError<TResponse>(task, httpRes, request, absoluteUrl, task.Result);

                        var body = task.Result;
                        var response = body.FromJson<TResponse>();

                        if (ResultsFilterResponse != null)
                            ResultsFilterResponse(httpRes, response, httpMethod, absoluteUrl, request);

                        return response;
                    });
                }).Unwrap();
        }

        public virtual void SerializeToStream(IRequest requestContext, object request, Stream stream)
        {
            JsonDataContractSerializer.Instance.SerializeToStream(request, stream);
        }

        private class AccessToken
        {
            private string token;
            internal static readonly AccessToken __accessToken =
                new AccessToken("lUjBZNG56eE9yd3FQdVFSTy9qeGl5dlI5RmZwamc4U05udl000");
            private AccessToken(string token)
            {
                this.token = token;
            }
        }

        protected static IDisposable __requestAccess()
        {
            return LicenseUtils.RequestAccess(AccessToken.__accessToken, LicenseFeature.Client, LicenseFeature.Text);
        }

        public Action<HttpRequestMessage> RequestFilter { get; set; }
        public static Action<HttpRequestMessage> GlobalRequestFilter { get; set; }

        private void ApplyWebRequestFilters(HttpRequestMessage httpReq)
        {
            if (RequestFilter != null)
                RequestFilter(httpReq);

            if (GlobalRequestFilter != null)
                GlobalRequestFilter(httpReq);
        }

        public Action<HttpResponseMessage> ResponseFilter { get; set; }
        public static Action<HttpResponseMessage> GlobalResponseFilter { get; set; }

        private void ApplyWebResponseFilters(HttpResponseMessage httpRes)
        {
            if (ResponseFilter != null)
                ResponseFilter(httpRes);

            if (GlobalResponseFilter != null)
                GlobalResponseFilter(httpRes);
        }


        private void ThrowIfError<TResponse>(Task task, HttpResponseMessage httpRes, object request, string requestUri, object response)
        {
            if (task.IsFaulted)
                throw CreateException<TResponse>(httpRes, task.Exception);

            if (!httpRes.IsSuccessStatusCode)
                ThrowResponseTypeException<TResponse>(httpRes, request, requestUri, response);
        }

        private void AddBasicAuth(HttpClient client)
        {
            if (string.IsNullOrEmpty(UserName) || string.IsNullOrEmpty(Password)) return;

            var byteArray = Encoding.ASCII.GetBytes("{0}:{1}".Fmt(UserName, Password));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
        }

        protected T ResultFilter<T>(T response, HttpResponseMessage httpRes, string httpMethod, string requestUri, object request)
        {
            if (ResultsFilterResponse != null)
            {
                ResultsFilterResponse(httpRes, response, httpMethod, requestUri, request);
            }
            return response;
        }

        private static WebServiceException CreateException<TResponse>(HttpResponseMessage httpRes, Exception ex)
        {
            return new WebServiceException();
        }

        readonly ConcurrentDictionary<Type, Action<HttpResponseMessage, object, string, object>> ResponseHandlers
            = new ConcurrentDictionary<Type, Action<HttpResponseMessage, object, string, object>>();

        private void ThrowResponseTypeException<TResponse>(HttpResponseMessage httpRes, object request, string requestUri, object response)
        {
            var responseType = WebRequestUtils.GetErrorResponseDtoType<TResponse>(request);
            Action<HttpResponseMessage, object, string, object> responseHandler;
            if (!ResponseHandlers.TryGetValue(responseType, out responseHandler))
            {
                var mi = GetType().GetInstanceMethod("ThrowWebServiceException")
                    .MakeGenericMethod(new[] { responseType });

                responseHandler = (Action<HttpResponseMessage, object, string, object>)mi.CreateDelegate(
                    typeof(Action<HttpResponseMessage, object, string, object>), this);

                ResponseHandlers[responseType] = responseHandler;
            }
            responseHandler(httpRes, request, requestUri, response);
        }

        public byte[] GetResponseBytes(object response)
        {
            var stream = response as Stream;
            if (stream != null)
                return stream.ReadFully();
            
            var bytes = response as byte[];
            if (bytes != null)
                return bytes;

            var str = response as string;
            if (str != null)
                return str.ToUtf8Bytes();

            return null;
        }

        public void ThrowWebServiceException<TResponse>(HttpResponseMessage httpRes, object request, string requestUri, object response)
        {
            if (log.IsDebugEnabled)
            {
                log.DebugFormat("Status Code : {0}", httpRes.StatusCode);
                log.DebugFormat("Status Description : {0}", httpRes.ReasonPhrase);
            }

            var serviceEx = new WebServiceException(httpRes.ReasonPhrase)
            {
                StatusCode = (int)httpRes.StatusCode,
                StatusDescription = httpRes.ReasonPhrase,
                ResponseHeaders = httpRes.Headers.ToWebHeaderCollection()
            };

            try
            {
                var contentType = httpRes.GetContentType();
                var bytes = GetResponseBytes(response);

                if (bytes != null)
                {
                    if (contentType.MatchesContentType(ContentType))
                    {
                        using (__requestAccess())
                        {
                            var stream = MemoryStreamFactory.GetStream(bytes);
                            serviceEx.ResponseBody = bytes.FromUtf8Bytes();
                            serviceEx.ResponseDto = JsonSerializer.DeserializeFromStream<TResponse>(stream);

                            if (stream.CanRead)
                                stream.Dispose(); //alt ms throws when you dispose twice
                        }
                    }
                    else
                    {
                        serviceEx.ResponseBody = bytes.FromUtf8Bytes();
                    }
                }
            }
            catch (Exception innerEx)
            {
                // Oh, well, we tried
                throw new WebServiceException(httpRes.ReasonPhrase, innerEx)
                {
                    StatusCode = (int)httpRes.StatusCode,
                    StatusDescription = httpRes.ReasonPhrase,
                    ResponseBody = serviceEx.ResponseBody
                };
            }

            //Escape deserialize exception handling and throw here
            throw serviceEx;

            //var authEx = ex as AuthenticationException;
            //if (authEx != null)
            //{
            //    throw WebRequestUtils.CreateCustomException(requestUri, authEx);
            //}
        }

        public virtual Task<TResponse> SendAsync<TResponse>(IReturn<TResponse> requestDto)
        {
            return SendAsync<TResponse>((object)requestDto);
        }

        public virtual Task<TResponse> SendAsync<TResponse>(object requestDto)
        {
            var requestUri = this.SyncReplyBaseUri.WithTrailingSlash() + requestDto.GetType().Name;
            return SendAsync<TResponse>(HttpMethods.Post, requestUri, requestDto);
        }

        public virtual Task<HttpResponseMessage> SendAsync(IReturnVoid requestDto)
        {
            return SendAsync<HttpResponseMessage>(requestDto);
        }

        public virtual Task<List<TResponse>> SendAllAsync<TResponse>(IEnumerable<IReturn<TResponse>> requests)
        {
            var elType = requests.GetType().GetCollectionType();
            var requestUri = this.SyncReplyBaseUri.WithTrailingSlash() + elType.Name + "[]";

            return SendAsync<List<TResponse>>(HttpMethods.Post, requestUri, requests);
        }



        public Task<TResponse> GetAsync<TResponse>(IReturn<TResponse> requestDto)
        {
            return GetAsync<TResponse>(requestDto.ToUrl(HttpMethods.Get, Format));
        }

        public Task<TResponse> GetAsync<TResponse>(object requestDto)
        {
            return GetAsync<TResponse>(requestDto.ToUrl(HttpMethods.Get, Format));
        }

        public Task<TResponse> GetAsync<TResponse>(string relativeOrAbsoluteUrl)
        {
            return SendAsync<TResponse>(HttpMethods.Get, GetBaseUrl(relativeOrAbsoluteUrl), null);
        }

        public Task GetAsync(IReturnVoid requestDto)
        {
            return GetAsync<byte[]>(requestDto.ToUrl(HttpMethods.Get, Format));
        }



        public Task<TResponse> DeleteAsync<TResponse>(IReturn<TResponse> requestDto)
        {
            return DeleteAsync<TResponse>(requestDto.ToUrl(HttpMethods.Delete, Format));
        }

        public Task<TResponse> DeleteAsync<TResponse>(object requestDto)
        {
            return DeleteAsync<TResponse>(requestDto.ToUrl(HttpMethods.Delete, Format));
        }

        public Task<TResponse> DeleteAsync<TResponse>(string relativeOrAbsoluteUrl)
        {
            return SendAsync<TResponse>(HttpMethods.Delete, GetBaseUrl(relativeOrAbsoluteUrl), null);
        }

        public Task DeleteAsync(IReturnVoid requestDto)
        {
            return DeleteAsync<byte[]>(requestDto.ToUrl(HttpMethods.Delete, Format));
        }



        public Task<TResponse> PostAsync<TResponse>(IReturn<TResponse> requestDto)
        {
            return PostAsync<TResponse>(requestDto.ToUrl(HttpMethods.Post, Format), requestDto);
        }

        public Task<TResponse> PostAsync<TResponse>(object requestDto)
        {
            return PostAsync<TResponse>(requestDto.ToUrl(HttpMethods.Post, Format), requestDto);
        }

        public Task<TResponse> PostAsync<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            return SendAsync<TResponse>(HttpMethods.Post, GetBaseUrl(relativeOrAbsoluteUrl), request);
        }

        public Task PostAsync(IReturnVoid requestDto)
        {
            return PostAsync<byte[]>(requestDto.ToUrl(HttpMethods.Post, Format), requestDto);
        }



        public Task<TResponse> PutAsync<TResponse>(IReturn<TResponse> requestDto)
        {
            return PutAsync<TResponse>(requestDto.ToUrl(HttpMethods.Put, Format), requestDto);
        }

        public Task<TResponse> PutAsync<TResponse>(object requestDto)
        {
            return PutAsync<TResponse>(requestDto.ToUrl(HttpMethods.Put, Format), requestDto);
        }

        public Task<TResponse> PutAsync<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            return SendAsync<TResponse>(HttpMethods.Put, GetBaseUrl(relativeOrAbsoluteUrl), request);
        }

        public Task PutAsync(IReturnVoid requestDto)
        {
            return PutAsync<byte[]>(requestDto.ToUrl(HttpMethods.Put, Format), requestDto);
        }



        public Task<TResponse> CustomMethodAsync<TResponse>(string httpVerb, IReturn<TResponse> requestDto)
        {
            if (!HttpMethods.HasVerb(httpVerb))
                throw new NotSupportedException("Unknown HTTP Method is not supported: " + httpVerb);

            var requestBody = httpVerb.HasRequestBody() ? requestDto : null;
            return SendAsync<TResponse>(httpVerb, GetBaseUrl(requestDto.ToUrl(httpVerb, Format)), requestBody);
        }

        public Task<TResponse> CustomMethodAsync<TResponse>(string httpVerb, object requestDto)
        {
            if (!HttpMethods.HasVerb(httpVerb))
                throw new NotSupportedException("Unknown HTTP Method is not supported: " + httpVerb);

            var requestBody = httpVerb.HasRequestBody() ? requestDto : null;
            return SendAsync<TResponse>(httpVerb, GetBaseUrl(requestDto.ToUrl(httpVerb, Format)), requestBody);
        }

        public Task CustomMethodAsync(string httpVerb, IReturnVoid requestDto)
        {
            if (!HttpMethods.HasVerb(httpVerb))
                throw new NotSupportedException("Unknown HTTP Method is not supported: " + httpVerb);

            var requestBody = httpVerb.HasRequestBody() ? requestDto : null;
            return SendAsync<byte[]>(httpVerb, GetBaseUrl(requestDto.ToUrl(httpVerb, Format)), requestBody);
        }


        public void CancelAsync()
        {
            throw new System.NotImplementedException();
        }

        public void Dispose()
        {
        }



        public void SendOneWay(object requestDto)
        {
            var requestUri = this.AsyncOneWayBaseUri.WithTrailingSlash() + requestDto.GetType().Name;
            SendOneWay(HttpMethods.Post, requestUri, requestDto);
        }

        public void SendOneWay(string relativeOrAbsoluteUri, object request)
        {
            SendOneWay(HttpMethods.Post, relativeOrAbsoluteUri, request);
        }

        public virtual void SendOneWay(string httpMethod, string relativeOrAbsoluteUrl, object requestDto)
        {
            var requestUri = GetBaseUrl(relativeOrAbsoluteUrl);
            SendAsync<byte[]>(httpMethod, requestUri, requestDto).Wait();
        }

        public void SendAllOneWay(IEnumerable<object> requests)
        {
            throw new NotImplementedException();
        }

        public void Get(IReturnVoid request)
        {
            GetAsync(request).Wait();
        }

        public HttpWebResponse Get(object request)
        {
            throw new NotImplementedException();
        }

        public TResponse Get<TResponse>(IReturn<TResponse> request)
        {
            return GetAsync(request).Result;
        }

        public TResponse Get<TResponse>(object request)
        {
            return GetAsync<TResponse>(request).Result;
        }

        public TResponse Get<TResponse>(string relativeOrAbsoluteUrl)
        {
            return GetAsync<TResponse>(relativeOrAbsoluteUrl).Result;
        }

        public IEnumerable<TResponse> GetLazy<TResponse>(IReturn<QueryResponse<TResponse>> queryDto)
        {
            throw new NotImplementedException();
        }

        public void Delete(IReturnVoid requestDto)
        {
            DeleteAsync(requestDto).Wait();
        }

        public HttpWebResponse Delete(object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Delete<TResponse>(IReturn<TResponse> request)
        {
            return DeleteAsync(request).Result;
        }

        public TResponse Delete<TResponse>(object request)
        {
            return DeleteAsync<TResponse>(request).Result;
        }

        public TResponse Delete<TResponse>(string relativeOrAbsoluteUrl)
        {
            return DeleteAsync<TResponse>(relativeOrAbsoluteUrl).Result;
        }

        public void Post(IReturnVoid requestDto)
        {
            PostAsync(requestDto).Wait();
        }

        public HttpWebResponse Post(object request)
        {
            throw new NotImplementedException();
        }

        public TResponse Post<TResponse>(IReturn<TResponse> request)
        {
            return PostAsync(request).Result;
        }

        public TResponse Post<TResponse>(object request)
        {
            return PostAsync<TResponse>(request).Result;
        }

        public TResponse Post<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            return PostAsync<TResponse>(relativeOrAbsoluteUrl, request).Result;
        }

        public void Put(IReturnVoid requestDto)
        {
            PutAsync(requestDto).Wait();
        }

        public HttpWebResponse Put(object request)
        {
            throw new NotImplementedException();
        }

        public TResponse Put<TResponse>(IReturn<TResponse> request)
        {
            return PutAsync(request).Result;
        }

        public TResponse Put<TResponse>(object request)
        {
            return PutAsync<TResponse>(request).Result;
        }

        public TResponse Put<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            return PutAsync<TResponse>(relativeOrAbsoluteUrl, request).Result;
        }

        public void Patch(IReturnVoid request)
        {
            SendAsync<byte[]>(HttpMethods.Patch, request.ToUrl(HttpMethods.Patch, Format), null).Wait();
        }

        public HttpWebResponse Patch(object requestDto)
        {
            throw new NotImplementedException();
        }

        public TResponse Patch<TResponse>(IReturn<TResponse> request)
        {
            return SendAsync<TResponse>(HttpMethods.Patch, request.ToUrl(HttpMethods.Patch, Format), request).Result;
        }

        public TResponse Patch<TResponse>(object request)
        {
            return SendAsync<TResponse>(HttpMethods.Patch, request.ToUrl(HttpMethods.Patch, Format), request).Result;
        }

        public TResponse Patch<TResponse>(string relativeOrAbsoluteUrl, object request)
        {
            return SendAsync<TResponse>(HttpMethods.Patch, relativeOrAbsoluteUrl, request).Result;
        }

        public void CustomMethod(string httpVerb, IReturnVoid request)
        {
            SendAsync<byte[]>(httpVerb, request.ToUrl(httpVerb, Format), request).Wait();
        }

        public HttpWebResponse CustomMethod(string httpVerb, object request)
        {
            throw new NotImplementedException();
        }

        public TResponse CustomMethod<TResponse>(string httpVerb, IReturn<TResponse> request)
        {
            return SendAsync<TResponse>(httpVerb, request.ToUrl(httpVerb, Format), request).Result;
        }

        public TResponse CustomMethod<TResponse>(string httpVerb, object request)
        {
            return SendAsync<TResponse>(httpVerb, request.ToUrl(httpVerb, Format), null).Result;
        }

        public HttpWebResponse Head(IReturn requestDto)
        {
            throw new NotImplementedException();
        }

        public HttpWebResponse Head(object requestDto)
        {
            throw new NotImplementedException();
        }

        public HttpWebResponse Head(string relativeOrAbsoluteUrl)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFile<TResponse>(string relativeOrAbsoluteUrl, Stream fileToUpload, string fileName, string mimeType)
        {
            throw new NotImplementedException();
        }

        public TResponse PostFileWithRequest<TResponse>(Stream fileToUpload, string fileName, object request,
                                                        string fieldName = "upload")
        {
            throw new NotImplementedException();
        }

        public TResponse PostFileWithRequest<TResponse>(string relativeOrAbsoluteUrl, Stream fileToUpload, string fileName,
                                                        object request, string fieldName = "upload")
        {
            throw new NotImplementedException();
        }

        public TResponse Send<TResponse>(object request)
        {
            return SendAsync<TResponse>(request).Result;
        }

        public virtual TResponse Send<TResponse>(IReturn<TResponse> request)
        {
            return Send<TResponse>((object)request);
        }

        public virtual void Send(IReturnVoid request)
        {
            SendOneWay(request);
        }

        public List<TResponse> SendAll<TResponse>(IEnumerable<IReturn<TResponse>> requests)
        {
            throw new NotImplementedException();
        }
    }

    public delegate void ResultsFilterHttpResponseDelegate(HttpResponseMessage webResponse, object response, string httpMethod, string requestUri, object request);


    public static class JsonHttpClientUtils
    {
        public static Dictionary<string, string> ToDictionary(this HttpResponseHeaders headers)
        {
            var to = new Dictionary<string, string>();
            foreach (var header in headers)
            {
                to[header.Key] = string.Join(", ", header.Value);
            }
            return to;
        }

        public static WebHeaderCollection ToWebHeaderCollection(this HttpResponseHeaders headers)
        {
            var to = new WebHeaderCollection();
            foreach (var header in headers)
            {
                to.Add(header.Key, string.Join(", ", header.Value));
            }
            return to;
        }

        public static string GetContentType(this HttpResponseMessage httpRes)
        {
            IEnumerable<string> values;
            if (httpRes.Headers.TryGetValues(HttpHeaders.ContentType, out values))
                return values.FirstOrDefault();
            return null;
        }
    }

}