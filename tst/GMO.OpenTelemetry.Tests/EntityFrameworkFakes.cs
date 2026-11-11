using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Scope = "namespace", Target = "~N:System.Data.Entity", Justification = "A stand-in for a third-party type; the code under test recognises it by this namespace.")]
[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Scope = "namespace", Target = "~N:Microsoft.EntityFrameworkCore", Justification = "A stand-in for a third-party type; the code under test recognises it by this namespace.")]

// Stand-ins for Entity Framework types, which the renderer and the log4net appender recognise by
// namespace. Each counts its enumerations: enumerating a real set or query runs it against the database.
namespace System.Data.Entity
{
    public class DbContext { }

    public sealed class DbSet<T> : IEnumerable<T>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return Enumerable.Empty<T>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace Microsoft.EntityFrameworkCore
{
    public class DbContext { }
}

namespace GMO.OpenTelemetry.Tests
{
    // A host's own context, in its own namespace: matched through its EF base type.
    public sealed class OrdersContext : System.Data.Entity.DbContext
    {
        public System.Data.Entity.DbSet<int> Orders { get; } = new System.Data.Entity.DbSet<int>();
    }

    // Counts reads rather than throwing: a throwing getter falls back to ToString(), which is the type
    // name, so a test asserting the name would pass even though the context was walked.
    public sealed class OrdersCoreContext : Microsoft.EntityFrameworkCore.DbContext
    {
        private int _reads;

        public int Reads() => _reads;

        public string Name
        {
            get { _reads++; return "core"; }
        }
    }

    public sealed class CountingQuery<T> : IQueryable<T>
    {
        public int Enumerations { get; private set; }

        public Type ElementType => typeof(T);

        public Expression Expression => Expression.Constant(this);

        public IQueryProvider Provider => throw new NotSupportedException();

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return Enumerable.Empty<T>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class ServiceHoldingData
    {
        public string Name { get; } = "svc";

        public OrdersContext Db { get; } = new OrdersContext();

        public System.Data.Entity.DbSet<int> Orders { get; } = new System.Data.Entity.DbSet<int>();

        public CountingQuery<int> Pending { get; } = new CountingQuery<int>();
    }
}
