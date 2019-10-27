using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace Activout.RestClient.DomainExceptions
{
    public abstract class AbstractDomainExceptionMapper : IDomainExceptionMapper
    {
        public virtual Task<Exception> CreateExceptionAsync(HttpResponseMessage httpResponseMessage, object data,
            Exception innerException)
        {
            return Task.FromResult(CreateException(httpResponseMessage, data, innerException));
        }

        protected virtual Exception CreateException(HttpResponseMessage httpResponseMessage, object data,
            Exception unused)
        {
            return CreateException(httpResponseMessage, data);
        }

        protected virtual Exception CreateException(HttpResponseMessage httpResponseMessage, object data)
        {
            throw new NotImplementedException();
        }
    }
}