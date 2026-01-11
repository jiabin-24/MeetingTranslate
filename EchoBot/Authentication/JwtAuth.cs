namespace EchoBot.Authentication
{
    public static class JwtAuth
    {
        public static bool ValidateToken(string? token, out string? userId)
        {
            userId = "example";
            // TODO: Implement JWT token validation logic here.
            return true;
        }
    }
}
