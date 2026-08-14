using AceLand.Injection;
using AceLand.Sample.Injection.Scripts.Services;

namespace AceLand.Sample.Injection.Scripts.Installers
{
    [AutoInstall]
    public sealed class ServicesAutoInstaller : IGlobalInstaller
    {
        public void Install(IContainerBuilder builder)
        {
            builder.Register<IScoreService, ScoreService>(Lifetime.Singleton);
            builder.RegisterEntryPoint<Helper>();
        }
    }
}