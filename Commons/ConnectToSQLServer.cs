using Microsoft.Extensions.Configuration;

namespace tzn_sumaken_shipment_schedule_delete_bat.Commons
{
    /// <summary>
    /// SQLServer接続に関する関数
    /// </summary>
    public static class ConnectToSQLServer
    {
        /// <summary>
        /// SQLServer接続文字列取得
        /// </summary>
        /// <returns></returns>
        public static string GetConnectionString(string key)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            var configuration = builder.Build();
            return configuration.GetSection("connectionString").GetValue<string>(key);
        }
    }
}