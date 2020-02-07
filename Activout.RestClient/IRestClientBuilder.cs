﻿using System;
using System.Net.Http;
using Activout.RestClient.DomainExceptions;
using Activout.RestClient.Helpers;
using Activout.RestClient.Serialization;

namespace Activout.RestClient
{
    public interface IRestClientBuilder
    {
        IRestClientBuilder BaseUri(Uri apiUri);
        IRestClientBuilder ContentType(MediaType contentType);
        IRestClientBuilder With(HttpClient httpClient);
        IRestClientBuilder Header(string name, object value);
        IRestClientBuilder With(IRequestLogger requestLogger);
        IRestClientBuilder With(IDeserializer deserializer);
        IRestClientBuilder With(ISerializer serializer);
        IRestClientBuilder With(ISerializationManager serializationManager);
        IRestClientBuilder With(ITaskConverterFactory taskConverterFactory);
        IRestClientBuilder With(IDomainExceptionMapperFactory domainExceptionMapperFactory);
        T Build<T>() where T : class;
    }
}