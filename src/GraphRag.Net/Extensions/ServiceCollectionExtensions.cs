using System.Reflection;
using GraphRag.Net;
using GraphRag.Net.Repositories;
using Microsoft.SemanticKernel;
using SqlSugar;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// 容器扩展
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 从程序集中加载类型并添加到容器中
        /// </summary>
        /// <param name="services">容器</param>
        /// <param name="_kernel">可选的自定义 pi.net 网关（可注入自定义 StreamFn 以对接其它模型或离线测试）</param>
        /// <returns></returns>
        public static IServiceCollection AddGraphRagNet(this IServiceCollection services, Kernel _kernel = null)
        {
            Type attributeType = typeof(ServiceDescriptionAttribute);

            Assembly assembly = Assembly.Load("GraphRag.Net");

            var types = assembly.GetTypes();

            foreach (var classType in types)
            {
                if (!classType.IsAbstract && classType.IsClass && classType.IsDefined(attributeType, false))
                {
                    ServiceDescriptionAttribute serviceAttribute = (classType.GetCustomAttribute(attributeType) as ServiceDescriptionAttribute);
                    switch (serviceAttribute.Lifetime)
                    {
                        case ServiceLifetime.Scoped:
                            services.AddScoped(serviceAttribute.ServiceType, classType);
                            break;

                        case ServiceLifetime.Singleton:
                            services.AddSingleton(serviceAttribute.ServiceType, classType);
                            break;

                        case ServiceLifetime.Transient:
                            services.AddTransient(serviceAttribute.ServiceType, classType);
                            break;
                    }
                }
            }

            CodeFirst();
            InitAi(services, _kernel);

            return services;
        }

        /// <summary>
        /// 初始化 pi.net AI 网关（原 InitSK）。
        /// 保留自定义 Kernel 参数以兼容既有调用方；未传入时按既有 GraphOpenAI 配置装配。
        /// 提示词仍从 graphPlugins 目录按需加载，无需预导入插件。
        /// </summary>
        static void InitAi(IServiceCollection services, Kernel _kernel = null)
        {
            services.AddTransient<Kernel>(_ => _kernel ?? new Kernel());
        }

        /// <summary>
        /// 初始化DB（会自动为 [SugarTable] 的向量记录实体建表）
        /// </summary>
        static void CodeFirst()
        {
            // 获取仓储服务
            var _repository = new Nodes_Repositories();

            // 创建数据库（如果不存在）
            _repository.GetDB().DbMaintenance.CreateDatabase();
            // 在所有程序集中查找具有[SugarTable]特性的类
            var assembly = Assembly.GetExecutingAssembly();
            // 获取该程序集中所有具有SugarTable特性的类型
            var entityTypes = assembly.GetTypes()
                    .Where(type => TypeIsEntity(type));
            // 为每个找到的类型初始化数据库表
            foreach (var type in entityTypes)
            {
                _repository.GetDB().CodeFirst.InitTables(type);
            }
        }

        static bool TypeIsEntity(Type type)
        {
            // 检查类型是否具有SugarTable特性
            return type.GetCustomAttributes(typeof(SugarTable), inherit: false).Length > 0;
        }
    }
}
