﻿using RATools.Data;
using RATools.Parser.Internal;
using System.Linq;

namespace RATools.Parser.Functions
{
    internal class MemoryAccessorFunction : TriggerBuilderContext.FunctionDefinition
    {
        public MemoryAccessorFunction(string name, FieldSize size)
            : base(name)
        {
            Size = size;

            Parameters.Add(new VariableDefinitionExpression("address"));
        }

        public FieldSize Size { get; private set; }

        public override bool ReplaceVariables(InterpreterScope scope, out ExpressionBase result)
        {
            if (!IsInTriggerClause(scope, out result))
                return false;

            var address = GetIntegerParameter(scope, "address", out result);
            if (address == null)
                return false;

            result = new FunctionCallExpression(Name.Name, new ExpressionBase[] { address });
            CopyLocation(result);
            return true;
        }

        public override ParseErrorExpression BuildTrigger(TriggerBuilderContext context, InterpreterScope scope, FunctionCallExpression functionCall)
        {
            var requirement = new Requirement();
            var address = (IntegerConstantExpression)functionCall.Parameters.First();
            requirement.Left = new Field { Size = this.Size, Type = FieldType.MemoryAddress, Value = (uint)address.Value };
            context.Trigger.Add(requirement);
            return null;
        }
    }
}
