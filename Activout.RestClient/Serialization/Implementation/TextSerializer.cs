﻿using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace Activout.RestClient.Serialization.Implementation
{
    class TextSerializer : ISerializer
    {
        public TextSerializer()
        {
            SupportedMediaTypes = new MediaTypeCollection()
            {
                new MediaTypeHeaderValue("text/*")
            };
        }

        public MediaTypeCollection SupportedMediaTypes { get; }

        public HttpContent Serialize(object data, Encoding encoding, string mediaType)
        {
            return new StringContent(data.ToString(), encoding, mediaType);
        }
    }
}
