﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Activout.RestClient.DomainExceptions;
using Activout.RestClient.Helpers;
using Activout.RestClient.ParamConverter;
using Activout.RestClient.Serialization;

namespace Activout.RestClient.Implementation
{
    internal class RequestHandler
    {
        // https://www.w3.org/Protocols/rfc2616/rfc2616-sec7.html#sec7.2.1
        private const string DefaultHttpContentType = "application/octet-stream";

        private readonly Type _actualReturnType;
        private readonly int _bodyArgumentIndex;
        private readonly MediaType _contentType;
        private readonly RestClientContext _context;
        private readonly ITaskConverter _converter;
        private readonly Type _errorResponseType;
        private readonly HttpMethod _httpMethod = HttpMethod.Get;
        private readonly ParameterInfo[] _parameters;
        private readonly Type _returnType;
        private readonly ISerializer _serializer;
        private readonly string _template;
        private readonly IParamConverter[] _paramConverters;
        private readonly IDomainExceptionMapper _domainExceptionMapper;
        private readonly List<KeyValuePair<string, object>> _requestHeaders = new List<KeyValuePair<string, object>>();

        public RequestHandler(MethodInfo method, RestClientContext context)
        {
            _returnType = method.ReturnType;
            _actualReturnType = GetActualReturnType();
            _parameters = method.GetParameters();
            _paramConverters = GetParamConverters(context.ParamConverterManager);
            _converter = CreateConverter(context);
            _template = context.BaseTemplate ?? "";
            _serializer = context.DefaultSerializer;
            _contentType = context.DefaultContentType;
            _errorResponseType = context.ErrorResponseType;
            _requestHeaders.AddRange(context.DefaultHeaders);

            _bodyArgumentIndex = _parameters.Length - 1;

            var templateBuilder = new StringBuilder(context.BaseTemplate ?? "");
            foreach (var attribute in method.GetCustomAttributes(true))
                switch (attribute)
                {
                    case ContentTypeAttribute contentTypeAttribute:
                        _contentType = MediaType.ValueOf(contentTypeAttribute.ContentType);
                        break;

                    case ErrorResponseAttribute errorResponseAttribute:
                        _errorResponseType = errorResponseAttribute.Type;
                        break;

                    case HeaderAttribute headerAttribute:
                        _requestHeaders.AddOrReplaceHeader(headerAttribute.Name, headerAttribute.Value,
                            headerAttribute.Replace);
                        break;

                    case HttpMethodAttribute httpMethodAttribute:
                        templateBuilder.Append(httpMethodAttribute.Template);
                        _httpMethod = GetHttpMethod(httpMethodAttribute);
                        break;

                    case RouteAttribute routeAttribute:
                        templateBuilder.Append(routeAttribute.Template);
                        break;
                }

            _serializer = context.SerializationManager.GetSerializer(_contentType);

            if (context.UseDomainException)
            {
                _domainExceptionMapper = context.DomainExceptionMapperFactory.CreateDomainExceptionMapper(
                    method,
                    _errorResponseType,
                    context.DomainExceptionType);
            }

            _template = templateBuilder.ToString();
            _context = context;
        }

        private IParamConverter[] GetParamConverters(IParamConverterManager paramConverterManager)
        {
            var paramConverters = new IParamConverter[_parameters.Length];
            for (var i = 0; i < _parameters.Length; i++)
            {
                paramConverters[i] = paramConverterManager.GetConverter(_parameters[i]);
            }

            return paramConverters;
        }

        private static HttpMethod GetHttpMethod(HttpMethodAttribute attribute)
        {
            return attribute.HttpMethod;
        }

        private ITaskConverter CreateConverter(RestClientContext context)
        {
            return context.TaskConverterFactory.CreateTaskConverter(_actualReturnType);
        }

        private bool IsVoidTask()
        {
            return _returnType == typeof(Task);
        }

        private bool IsGenericTask()
        {
            return _returnType.BaseType == typeof(Task) && _returnType.IsGenericType;
        }

        private Type GetActualReturnType()
        {
            if (IsVoidTask())
                return typeof(void);
            if (IsGenericTask())
                return _returnType.GenericTypeArguments[0];
            return _returnType;
        }

        private string ExpandTemplate(Dictionary<string, object> routeParams)
        {
            var expanded = _template;
            foreach (var entry in routeParams)
                expanded = expanded.Replace("{" + entry.Key + "}", entry.Value.ToString());

            return expanded;
        }

        // Based on PrepareRequestMessage at https://github.com/dotnet/corefx/blob/master/src/System.Net.Http/src/System/Net/Http/HttpClient.cs
        private void PrepareRequestMessage(HttpRequestMessage request)
        {
            var baseUri = _context.BaseUri;
            Uri requestUri = null;
            if (request.RequestUri == null && baseUri == null) throw new InvalidOperationException();
            if (request.RequestUri == null)
            {
                requestUri = baseUri;
            }
            else
            {
                // If the request Uri is an absolute Uri, just use it. Otherwise try to combine it with the base Uri.
                if (!request.RequestUri.IsAbsoluteUri)
                {
                    if (baseUri == null)
                        throw new InvalidOperationException();
                    requestUri = new Uri(baseUri, request.RequestUri);
                }
            }

            // We modified the original request Uri. Assign the new Uri to the request message.
            if (requestUri != null) request.RequestUri = requestUri;
        }

        public object Send(object[] args)
        {
            var headers = new List<KeyValuePair<string, object>>();
            headers.AddRange(_requestHeaders);

            var routeParams = new Dictionary<string, object>();
            var queryParams = new List<string>();
            var formParams = new List<KeyValuePair<string, string>>();
            var cancellationToken = GetParams(args, routeParams, queryParams, formParams, headers);

            var requestUriString = ExpandTemplate(routeParams);
            if (queryParams.Any())
            {
                requestUriString = requestUriString + "?" + string.Join("&", queryParams);
            }

            var requestUri = new Uri(requestUriString, UriKind.RelativeOrAbsolute);

            var request = new HttpRequestMessage(_httpMethod, requestUri);

            SetHeaders(request, headers);

            if (_httpMethod == HttpMethod.Post || _httpMethod == HttpMethod.Put)
            {
                if (formParams.Any())
                {
                    request.Content = new FormUrlEncodedContent(formParams);
                }
                else if (args[_bodyArgumentIndex] is HttpContent httpContent)
                {
                    request.Content = httpContent;
                }
                else
                {
                    if (_serializer == null)
                    {
                        throw new InvalidOperationException("No serializer for: " + _contentType);
                    }

                    request.Content = _serializer.Serialize(args[_bodyArgumentIndex], Encoding.UTF8, _contentType);
                }
            }

            var task = SendAsync(request, cancellationToken);

            if (IsVoidTask())
                return task;
            if (_returnType.BaseType == typeof(Task) && _returnType.IsGenericType)
                return _converter.ConvertReturnType(task);
            return task.Result;
        }

        private void SetHeaders(HttpRequestMessage request, List<KeyValuePair<string, object>> headers)
        {
            headers.ForEach(p => request.Headers.Add(p.Key, p.Value.ToString()));
        }

        private CancellationToken GetParams(IReadOnlyList<object> args, IDictionary<string, object> routeParams,
            ICollection<string> queryParams, ICollection<KeyValuePair<string, string>> formParams,
            List<KeyValuePair<string, object>> headers)
        {
            var cancellationToken = CancellationToken.None;

            for (var i = 0; i < _parameters.Length; i++)
            {
                if (args[i] is CancellationToken ct)
                {
                    cancellationToken = ct;
                    continue;
                }

                var parameterAttributes = _parameters[i].GetCustomAttributes(false);
                var name = _parameters[i].Name;
                var value = _paramConverters[i].ToString(args[i]);
                var escapedValue = Uri.EscapeDataString(value);
                var handled = false;

                foreach (var attribute in parameterAttributes)
                {
                    if (attribute is RouteParamAttribute routeParamAttribute)
                    {
                        routeParams[routeParamAttribute.Name ?? name] = escapedValue;
                        handled = true;
                    }
                    else if (attribute is QueryParamAttribute queryParamAttribute)
                    {
                        name = queryParamAttribute.Name ?? name;
                        queryParams.Add(Uri.EscapeDataString(name) + "=" + escapedValue);
                        handled = true;
                    }
                    else if (attribute is FormParamAttribute formParamAttribute)
                    {
                        name = formParamAttribute.Name ?? name;
                        formParams.Add(new KeyValuePair<string, string>(name, value));
                        handled = true;
                    }
                    else if (attribute is HeaderParamAttribute headerParamAttribute)
                    {
                        name = headerParamAttribute.Name ?? name;
                        headers.AddOrReplaceHeader(name, value, headerParamAttribute.Replace);
                        handled = true;
                    }
                }

                if (!handled)
                {
                    routeParams[name] = escapedValue;
                }
            }

            return cancellationToken;
        }

        private async Task<object> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            PrepareRequestMessage(request);

            HttpResponseMessage response;
            using (_context.RequestLogger.TimeOperation(request))
            {
                response = await _context.HttpClient.SendAsync(request, cancellationToken);
            }

            if (_actualReturnType == typeof(HttpStatusCode))
            {
                return response.StatusCode;
            }

            if (_actualReturnType == typeof(HttpResponseMessage))
            {
                return response;
            }

            var data = await GetResponseData(request, response);

            if (response.IsSuccessStatusCode)
            {
                return data;
            }

            if (_context.UseDomainException)
            {
                throw await _domainExceptionMapper.CreateExceptionAsync(response, data);
            }

            throw new RestClientException(request.RequestUri, response.StatusCode, data);
        }

        private async Task<object> GetResponseData(HttpRequestMessage request, HttpResponseMessage response)
        {
            var type = response.IsSuccessStatusCode ? _actualReturnType : _errorResponseType;

            if (type == typeof(void) || response.Content == null)
            {
                return null;
            }

            // HttpContent or a subclass like MultipartFormDataContent
            if (type.IsInstanceOfType(response.Content))
            {
                return response.Content;
            }

            var contentTypeMediaType = response.Content.Headers?.ContentType?.MediaType ?? DefaultHttpContentType;
            var deserializer = _context.SerializationManager.GetDeserializer(new MediaType(contentTypeMediaType));
            if (deserializer == null)
            {
                throw await CreateNoDeserializerFoundException(request, response, contentTypeMediaType);
            }

            try
            {
                return await deserializer.Deserialize(response.Content, type);
            }
            catch (Exception e)
            {
                if (e is RestClientException)
                {
                    throw;
                }

                throw await CreateDeserializationException(request, response, e);
            }
        }

        private async Task<Exception> CreateDeserializationException(HttpRequestMessage request,
            HttpResponseMessage response, Exception e)
        {
            var errorResponse = response.Content == null ? null : await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode || !_context.UseDomainException)
            {
                return new RestClientException(request.RequestUri, response.StatusCode, errorResponse, e);
            }

            return await _domainExceptionMapper.CreateExceptionAsync(response, errorResponse, e);
        }

        private async Task<Exception> CreateNoDeserializerFoundException(HttpRequestMessage request,
            HttpResponseMessage response,
            string contentTypeMediaType)
        {
            var exception = (Exception) new RestClientException(request.RequestUri, response.StatusCode,
                "No deserializer found for " + contentTypeMediaType);

            if (response.IsSuccessStatusCode || !_context.UseDomainException)
            {
                return exception;
            }

            return await _domainExceptionMapper.CreateExceptionAsync(response, null, exception);
        }
    }
}