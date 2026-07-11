using Api.Framework;
using NewWords.Api.Entities;
using SqlSugar;

namespace NewWords.Api.Repositories
{
    public class UserRepository(ISqlSugarClient dbClient) : RepositoryBase<User>(dbClient), IUserRepository
    {
        public async Task<User?> GetByIdAsync(long userId)
        {
            return await GetSingleAsync(userId);
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await GetFirstOrDefaultAsync(u => u.Email == email);
        }
    }
}
