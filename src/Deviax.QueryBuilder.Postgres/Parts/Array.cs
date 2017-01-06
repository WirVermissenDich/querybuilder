﻿using Deviax.QueryBuilder.Visitors;

namespace Deviax.QueryBuilder.Parts
{
    public class ArrayAggPart : Part
    {
        public readonly IPart Over;

        public ArrayAggPart(IPart over)
        {
            Over = over;
        }

        public override void Accept(INodeVisitor visitor) => visitor.Visit(this);
    }
}