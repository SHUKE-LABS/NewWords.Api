using Api.Framework;
using NewWords.Api.Entities;

namespace NewWords.Api.Repositories
{
    public interface IUserRepository : IRepositoryBase<User>
    {
        Task<User?> GetByIdAsync(long userId);
        Task<User?> GetByEmailAsync(string email);
    }
}
