// Build-time stub for Datev.Sdd.Data.ClientInterfaces.
// At runtime the real assembly is loaded from the GAC by GacAssemblyResolver.
using System;
using System.Xml;

namespace Datev.Sdd.Data.ClientInterfaces
{
    public interface IRequestHandler
    {
        Response Execute(Request request);
    }

    public interface IRequestHelper
    {
        Request CreateDataObjectCollectionAccessReadRequest(
            string elementName, string contractIdentifier,
            string dataEnvironment, string filterExpression);
    }

    public class Request { }

    public class Response : IDisposable
    {
        public bool HasData { get; }
        public XmlReader CreateReader() => null;
        public void Dispose() { }
    }
}
