#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace BattleSystemECS.Tests.Infrastructure
{
    internal static class ProductionIlWalker
    {
        public static IReadOnlyList<MemberInfo> GetMetadataReferences(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            MethodBody methodBody = method.GetMethodBody();
            if (methodBody == null)
                throw new InvalidOperationException("method has no IL body: " + method);
            byte[] body = methodBody.GetILAsByteArray()
                ?? throw new InvalidOperationException("method IL body is unavailable: " + method);
            var references = new List<MemberInfo>();
            Walk(method, body, (operandType, token, offset) =>
            {
                MemberInfo resolved = ResolveMember(method, token, operandType, offset);
                if (!ReferenceEquals(resolved, method)) references.Add(resolved);
            });
            return references;
        }

        public static IReadOnlyList<MethodBase> GetCalledMethods(MethodBase method)
        {
            var called = new List<MethodBase>();
            foreach (MemberInfo reference in GetMetadataReferences(method))
                if (reference is MethodBase calledMethod) called.Add(calledMethod);
            return called;
        }

        private static void Walk(MethodBase method, byte[] body, Action<OperandType, int, int> metadata)
        {
            var oneByte = new Dictionary<byte, OpCode>();
            var twoByte = new Dictionary<byte, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode code) continue;
                ushort value = unchecked((ushort)code.Value);
                if (value < 0x100) oneByte[(byte)value] = code;
                else if ((value & 0xff00) == 0xfe00) twoByte[(byte)value] = code;
            }

            int offset = 0;
            while (offset < body.Length)
            {
                int instructionOffset = offset;
                OpCode code = ReadOpCode(body, ref offset, oneByte, twoByte, method);
                int operandSize = GetOperandSize(code.OperandType, body, offset, method);
                EnsureAvailable(body, offset, operandSize, method, instructionOffset);

                if (code.OperandType == OperandType.InlineMethod || code.OperandType == OperandType.InlineTok ||
                    code.OperandType == OperandType.InlineField || code.OperandType == OperandType.InlineType ||
                    code.OperandType == OperandType.InlineSig || code.OperandType == OperandType.InlineString)
                {
                    int token = BitConverter.ToInt32(body, offset);
                    if (token == 0) throw Failure("invalid zero metadata token", method, instructionOffset);
                    metadata(code.OperandType, token, instructionOffset);
                }
                offset += operandSize;
            }
        }

        public static HashSet<MethodBase> CollectTransitiveCalls(IEnumerable<MethodBase> roots,
            params Assembly[] projectAssemblies)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            if (projectAssemblies == null) throw new ArgumentNullException(nameof(projectAssemblies));
            var assemblies = new HashSet<Assembly>(projectAssemblies);
            var result = new HashSet<MethodBase>();
            var visited = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>(roots);
            while (queue.Count > 0)
            {
                MethodBase method = queue.Dequeue();
                if (!visited.Add(method)) continue;
                if (method.GetMethodBody() == null) continue;
                foreach (MethodBase called in GetCalledMethods(method))
                {
                    result.Add(called);
                    if (called.DeclaringType != null && assemblies.Contains(called.DeclaringType.Assembly))
                        queue.Enqueue(called);
                }
            }
            return result;
        }

        public static bool FindCalls(MethodInfo root, Func<MethodBase, bool> predicate,
            params Assembly[] projectAssemblies)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            foreach (MethodBase method in CollectTransitiveCalls(new[] { root }, projectAssemblies))
                if (predicate(method)) return true;
            return false;
        }

        private static OpCode ReadOpCode(byte[] body, ref int offset,
            Dictionary<byte, OpCode> oneByte, Dictionary<byte, OpCode> twoByte, MethodBase method)
        {
            int at = offset;
            byte first = body[offset++];
            if (first == 0xfe)
            {
                if (offset >= body.Length || !twoByte.TryGetValue(body[offset++], out OpCode code))
                    throw Failure("unknown two-byte opcode", method, at);
                return code;
            }
            if (!oneByte.TryGetValue(first, out OpCode one))
                throw Failure("unknown opcode", method, at);
            return one;
        }

        private static int GetOperandSize(OperandType type, byte[] body, int offset, MethodBase method)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineR: return 4;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    EnsureAvailable(body, offset, 4, method, offset);
                    int count = BitConverter.ToInt32(body, offset);
                    if (count < 0 || count > (body.Length - offset - 4) / 4)
                        throw Failure("invalid switch operand", method, offset);
                    return 4 + count * 4;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType: return 4;
                default: throw Failure("unsupported operand type " + type, method, offset);
            }
        }

        private static MemberInfo ResolveMember(MethodBase method, int token, OperandType type, int offset)
        {
            Type[] typeContext = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodContext = method.IsGenericMethod ? method.GetGenericArguments() : null;
            try
            {
                if (type == OperandType.InlineMethod)
                    return method.Module.ResolveMethod(token, typeContext, methodContext);
                if (type == OperandType.InlineTok)
                    return method.Module.ResolveMember(token, typeContext, methodContext);
                if (type == OperandType.InlineField)
                    return method.Module.ResolveField(token, typeContext, methodContext);
                if (type == OperandType.InlineType)
                    return method.Module.ResolveType(token, typeContext, methodContext);
                if (type == OperandType.InlineString)
                {
                    method.Module.ResolveString(token);
                    return method;
                }
                method.Module.ResolveSignature(token);
                return method;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("unresolved metadata token 0x" + token.ToString("X8") +
                    " at IL_" + offset.ToString("X4") + " in " + method, error);
            }
        }

        private static void EnsureAvailable(byte[] body, int offset, int size, MethodBase method, int instructionOffset)
        {
            if (offset < 0 || size < 0 || offset > body.Length - size)
                throw Failure("truncated IL operand", method, instructionOffset);
        }

        private static InvalidOperationException Failure(string message, MethodBase method, int offset) =>
            new InvalidOperationException(message + " at IL_" + offset.ToString("X4") + " in " + method);
    }
}
