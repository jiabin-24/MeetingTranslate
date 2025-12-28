namespace EchoBot.Util
{
    public static class ServiceLocator
    {
        private static IServiceProvider? _root;
        public static void Initialize(IServiceProvider root) => _root = root;

        public static T GetRequiredService<T>() where T : notnull
        {
            if (_root is null) throw new InvalidOperationException("ServiceLocator not initualize");
            using var scope = _root.CreateScope();
            return scope.ServiceProvider.GetRequiredService<T>();
        }
    }
}
