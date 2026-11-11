using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Xunit;

[assembly: SuppressMessage("Style", "IDE0130:Namespace does not match folder structure", Scope = "namespace", Target = "~N:System.Data.Linq.Mapping", Justification = "A stand-in for a third-party type; the code under test recognises it by this namespace.")]

namespace System.Data.Linq.Mapping
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class AssociationAttribute : Attribute
    {
    }
}

namespace GMO.OpenTelemetry.Tests
{
    [Collection("OpenTelemetryEnricherState")]
    public class ObjectRendererLinqToSqlTests
    {
        [Fact]
        public void AssociationProperties_AreNeverRead()
        {
            var parent = new Parent { Id = 7, Name = "p" };

            var json = ObjectRendererTests.Json(ObjectRenderer.Render(parent, 3));

            Assert.Equal(7, json.GetProperty("Id").GetInt32());
            Assert.Equal("p", json.GetProperty("Name").GetString());
            Assert.False(json.TryGetProperty("Children", out _));
            Assert.False(json.TryGetProperty("Owner", out _));
            Assert.Equal(0, parent.ChildrenReads);
            Assert.Equal(0, parent.OwnerReads);
        }

        [Fact]
        public void AssociationProperties_AreSkippedBelowTheRoot()
        {
            var holder = new { Entity = new Parent { Id = 1 } };

            var json = ObjectRendererTests.Json(ObjectRenderer.Render(holder, 3));

            var entity = json.GetProperty("Entity");
            Assert.Equal(1, entity.GetProperty("Id").GetInt32());
            Assert.False(entity.TryGetProperty("Children", out _));
            Assert.Equal(0, holder.Entity.ChildrenReads);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        public void AssociationProperties_AreNeverRead_AtAnyDepth(int depth)
        {
            var parent = new Parent { Id = 7 };

            var json = ObjectRendererTests.Json(ObjectRenderer.Render(parent, depth));

            Assert.False(json.TryGetProperty("Children", out _));
            Assert.False(json.TryGetProperty("Owner", out _));
            Assert.Equal(0, parent.ChildrenReads);
            Assert.Equal(0, parent.OwnerReads);
            Assert.Equal(depth > 1 ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("Id").ValueKind);
        }

        public sealed class Parent
        {
            private int _childrenReads;
            private int _ownerReads;

            public int Id { get; set; }

            public string? Name { get; set; }

            public int ChildrenReads => _childrenReads;

            public int OwnerReads => _ownerReads;

            [System.Data.Linq.Mapping.Association]
            public IEnumerable<string> Children
            {
                get { _childrenReads++; return ["c"]; }
            }

            [System.Data.Linq.Mapping.Association]
            public Parent? Owner
            {
                get { _ownerReads++; return null; }
            }
        }
    }
}
