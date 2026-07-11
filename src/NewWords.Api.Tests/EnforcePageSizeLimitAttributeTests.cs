using System.Collections.Generic;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace NewWords.Api.Tests
{
    public class EnforcePageSizeLimitAttributeTests
    {
        private static IDictionary<string, object?> Invoke(int maxPageSize, Dictionary<string, object?> arguments)
        {
            var actionContext = new ActionContext(
                new DefaultHttpContext(),
                new RouteData(),
                new ActionDescriptor());

            var context = new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                arguments!,
                controller: null!);

            new EnforcePageSizeLimitAttribute(maxPageSize).OnActionExecuting(context);
            return context.ActionArguments;
        }

        [Fact]
        public void OverCapPageSize_IsClampedToMax()
        {
            var args = Invoke(100, new() { ["pageSize"] = 1_000_000, ["pageNumber"] = 1 });
            args["pageSize"].Should().Be(100);
            args["pageNumber"].Should().Be(1);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void NonPositivePageSize_IsClampedToOne(int pageSize)
        {
            var args = Invoke(100, new() { ["pageSize"] = pageSize, ["pageNumber"] = 1 });
            args["pageSize"].Should().Be(1);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void NonPositivePageNumber_IsClampedToOne(int pageNumber)
        {
            var args = Invoke(100, new() { ["pageSize"] = 10, ["pageNumber"] = pageNumber });
            args["pageNumber"].Should().Be(1);
        }

        [Fact]
        public void InRangeValues_AreUnchanged()
        {
            var args = Invoke(100, new() { ["pageSize"] = 50, ["pageNumber"] = 3 });
            args["pageSize"].Should().Be(50);
            args["pageNumber"].Should().Be(3);
        }

        [Fact]
        public void MissingArguments_DoNotThrow()
        {
            var act = () => Invoke(100, new());
            act.Should().NotThrow();
        }
    }
}
