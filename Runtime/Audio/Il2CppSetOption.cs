// IL2CPP узнаёт атрибут по полному имени типа, а не по сборке: объявляем свою копию (internal),
// чтобы снять проверки null и границ массивов на горячем пути. Границы проверяем сами одним сравнением.
namespace Unity.IL2CPP.CompilerServices
{
    internal enum Option
    {
        NullChecks = 1,
        ArrayBoundsChecks = 2,
        DivideByZeroChecks = 3,
    }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Method |
                           System.AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class Il2CppSetOptionAttribute : System.Attribute
    {
        public Option Option { get; }
        public object Value { get; }

        public Il2CppSetOptionAttribute(Option option, object value)
        {
            Option = option;
            Value = value;
        }
    }
}
