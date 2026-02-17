// Build-time stub for Datev.Sdd.Data.ClientPlugIn.Base.
// At runtime the real assembly is loaded from the GAC by GacAssemblyResolver.
using System;
using Datev.Sdd.Data.ClientInterfaces;

namespace Datev.Sdd.Data.ClientPlugIn
{
    public class Proxy : IDisposable
    {
        public static Proxy Instance => null;
        public IRequestHandler RequestHandler => null;
        public IRequestHelper RequestHelper => null;
        public void Dispose() { }
    }
}
