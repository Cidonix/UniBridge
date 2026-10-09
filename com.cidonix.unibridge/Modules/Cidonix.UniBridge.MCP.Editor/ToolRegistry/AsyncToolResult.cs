using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry
{
    static class AsyncToolResult
    {
        public static void ValidateMethod(MethodInfo method)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));
            if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() != null)
                throw new ArgumentException("async void tools cannot provide a tracked completion outcome.");
            var payload = OutputContractProvider.UnwrapAsync(method.ReturnType);
            if (payload != method.ReturnType && (typeof(Task).IsAssignableFrom(payload) || IsValueTask(payload)))
                throw new ArgumentException("Nested async result declarations are unsupported; return one awaited payload.");
        }

        static bool IsValueTask(Type type) => type == typeof(ValueTask) ||
            (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>));

        public static async Task<object> Await(object result, Type declaredReturnType = null)
        {
            if (result is ValueTask emptyValueTask)
            {
                await emptyValueTask;
                return null;
            }
            if (result != null && result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                // Convert a boxed ValueTask<T> exactly once; IValueTaskSource may allow only one consumption.
                result = result.GetType().GetMethod("AsTask", Type.EmptyTypes).Invoke(result, null);
            }
            if (result is Task task)
            {
                await task;
                if (declaredReturnType == typeof(Task) || declaredReturnType == typeof(ValueTask)) return null;
                var type = task.GetType();
                while (type != null && (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>)))
                    type = type.BaseType;
                return type == null ? null : type.GetProperty("Result").GetValue(task);
            }
            return result;
        }

        public static MethodInfo InterfaceExecute(object instance, Type parameterType)
        {
            var contract = parameterType == null ? typeof(IUnityMcpTool) : typeof(IUnityMcpTool<>).MakeGenericType(parameterType);
            var map = instance.GetType().GetInterfaceMap(contract);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
                if (map.InterfaceMethods[i].Name == "ExecuteAsync")
                    return map.TargetMethods[i];
            throw new MissingMethodException("The registered tool has no matching interface ExecuteAsync implementation.");
        }
    }
}
