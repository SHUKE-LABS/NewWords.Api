using AutoMapper;
using FluentAssertions;
using NewWords.Api.MappingProfiles;
using Xunit;

namespace NewWords.Api.Tests.MappingProfiles
{
    /// <summary>
    /// Guards against GHSA-rvv3-g6hj-g44x / CVE-2026-32933: AutoMapper's
    /// uncontrolled-recursion DoS. Proves the production recursion guard bounds
    /// mapping depth so a pathologically deep self-referencing graph faults
    /// gracefully instead of stack-overflowing the process.
    /// </summary>
    public class AutoMapperRecursionGuardTests
    {
        // Self-referencing shape — the exact class of graph the advisory abuses.
        private sealed class Node
        {
            public int Value { get; set; }
            public Node? Next { get; set; }
        }

        private static Node BuildChain(int depth)
        {
            var head = new Node { Value = 0 };
            var current = head;
            for (var i = 1; i < depth; i++)
            {
                current.Next = new Node { Value = i };
                current = current.Next;
            }
            return head;
        }

        private static int MeasureDepth(Node? node)
        {
            var depth = 0;
            while (node is not null)
            {
                depth++;
                node = node.Next;
            }
            return depth;
        }

        private static IMapper CreateMapper()
        {
            var config = new MapperConfiguration(cfg =>
            {
                cfg.CreateMap<Node, Node>();
                // Exercise the exact guard Program.cs applies in production.
                AutoMapperConfiguration.ApplyRecursionGuard(cfg);
            });
            return config.CreateMapper();
        }

        [Fact]
        public void Mapping_DeeplyNestedGraph_TruncatesAtMaxDepth_WithoutStackOverflow()
        {
            // Far deeper than the bound; would overflow the stack unmapped.
            var source = BuildChain(20_000);
            var mapper = CreateMapper();

            var act = () => mapper.Map<Node>(source);

            var result = act.Should().NotThrow(
                "the global MaxDepth bound must fault deep graphs gracefully rather than stack-overflow")
                .Subject;
            result.Should().NotBeNull();
            // Bounded to the configured depth — deeper nodes are truncated to null.
            MeasureDepth(result).Should().Be(AutoMapperConfiguration.MaxRecursionDepth);
        }

        [Fact]
        public void Mapping_ShallowGraph_MapsFully()
        {
            var source = BuildChain(3);
            var mapper = CreateMapper();

            var result = mapper.Map<Node>(source);

            MeasureDepth(result).Should().Be(3);
            result.Next!.Next!.Value.Should().Be(2);
        }
    }
}
