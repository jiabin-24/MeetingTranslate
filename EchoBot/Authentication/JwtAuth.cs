using System.IdentityModel.Tokens.Jwt;

namespace EchoBot.Authentication
{
    public static class JwtAuth
    {
        public static bool ValidateToken(string? token, out string? userId)
        {
            userId = null;
            if (string.IsNullOrWhiteSpace(token)) return false;

            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);
                // TODO: 在生产环境使用 TokenValidationParameters 验证签名、issuer、audience、过期时间等
                userId = jwt.Subject; // sub
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
