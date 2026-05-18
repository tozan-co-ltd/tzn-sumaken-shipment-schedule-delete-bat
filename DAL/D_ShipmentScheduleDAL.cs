using Dapper;
using System.Data.SqlClient;
using tzn_sumaken_shipment_schedule_delete_bat.Commons;
using static tzn_sumaken_shipment_schedule_delete_bat.Commons.SystemConstants;

namespace tzn_sumaken_shipment_schedule_delete_bat.DAL
{
    internal class D_ShipmentScheduleDAL
    {
        /// <summary>
        /// 出荷指示登録
        /// </summary>
        public static void ImportShipmentScheduleFromEDI()
        {
            try
            {
                using var conn = new SqlConnection(
                    ConnectToSQLServer.GetConnectionString("warehouse"));

                conn.Open();

                // データ取得
                var targets = GetShipmentScheduleTargets(conn);

                // OKデータ
                var okList = targets
                    .Where(x => x.ErrorMessage == null)
                    .ToList();

                // NGデータ
                var ngList = targets
                    .Where(x => x.ErrorMessage != null)
                    .ToList();

                // 登録
                var affected = InsertShipmentSchedules(conn, okList);

                Console.WriteLine($"取込件数：{affected}件" + Environment.NewLine);

                if (ngList.Any())
                {
                    Console.WriteLine($"エラー件数：{ngList.Count}件");

                    // エラーログ出力
                    OutputErrorLog(ngList);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        /// <summary>
        /// データ取得
        /// </summary>
        /// <param name="conn"></param>
        /// <returns></returns>
        private static List<dynamic> GetShipmentScheduleTargets(SqlConnection conn)
        {
            var sql = @"
                SELECT
                    @DepoID AS DepoID,

                    mc.CompanyID,

                    @DeliveryTimeClass AS DeliveryTimeClass,

                    CASE
                        WHEN h.VHJIKO = 'L6'
                            THEN N'三菱KD工場'
                        ELSE m.DEKANJ
                    END AS DeliveryName,

                    md.DeliveryCode,

                    md.DeliveryFactoryKubun,

                    e.VINONO AS DeliverySlipNumber,

                    e.VIBUNM AS DeliveryProductName,

                    e.VIBUNO AS DeliveryProductNumber,

                    e.VIBUNO AS SupplierProductNumber,

                    e.VISRYO AS Quantity,

                    ISNULL(
                        CEILING(
                            CAST(e.VISRYO AS DECIMAL(18, 2))
                            / NULLIF(p.LotQuantity, 0)
                        ),
                        0
                    ) AS NumberOfBoxes,

                    h.VHNOBA AS DeliveryFactoryName,

                    e.VIDATE AS DeliveryDate,

                    h.VHJIKO AS DeliveryLocation,

                    p.LotQuantity,

                    GETDATE() AS IssuedDate,

                    e.TorokuDateTime AS CreatedAt,

                    e.KosinUserId AS CreatedBy,

                    GETDATE() AS UpdatedAt,

                    e.KosinUserId AS UpdatedBy,

                    CASE
                        WHEN mc.CompanyID IS NULL
                            THEN N'Companyマスタ不一致'

                        WHEN md.DeliveryCode IS NULL
                            THEN N'Deliveryマスタ不一致'

                        WHEN p.LotQuantity IS NULL
                            THEN N'Productマスタ不一致'

                        WHEN p.LotQuantity = 0
                            THEN N'LotQuantityが0'

                        ELSE NULL
                    END AS ErrorMessage

                FROM tozandbEDI.dbo.EDI_VI_nohin_meisai e

                INNER JOIN tozandbEDI.dbo.EDI_VH_nohin_header h
                    ON  e.VINOKU = h.VHNOKU
                    AND e.VINOSE = h.VHNOSE
                    AND e.VINONO = h.VHNONO

                LEFT JOIN tozandbEDI.dbo.BU_DE_UNYname_master m
                    ON m.DEMECD = h.VHJIKO

                LEFT JOIN M_Company mc
                    ON mc.CompanyName =
                        CASE
                            WHEN h.VHNOSE IN ('T', 'B', 'M', 'N')
                                THEN N'三菱ふそう'
                            ELSE N'三菱自動車'
                        END
                    AND mc.IsDeleted = 0

                LEFT JOIN M_Delivery md
                    ON  md.CompanyID = mc.CompanyID
                    AND md.DeliveryName =
                        CASE
                            WHEN h.VHJIKO = 'L6'
                                THEN N'三菱KD工場'
                            ELSE m.DEKANJ
                        END
                    AND md.DeliveryFactoryName = h.VHNOBA
                    AND md.IsDeleted = 0

                LEFT JOIN M_Product p
                    ON  p.SupplierProductNumber = e.VIBUNO
                    AND p.CompanyID = mc.CompanyID
                    AND p.IsDeleted = 0

                WHERE
                    e.VITRCD = 'J019'

                    AND e.TorokuDateTime >= DATEADD(DAY, -2, GETDATE())

                    AND NOT EXISTS
                    (
                        SELECT 1
                        FROM D_ShipmentSchedule d
                        WHERE d.DeliverySlipNumber = e.VINONO
                    )
            ";

            return conn.Query<dynamic>(sql, new
            {
                DepoID = Mitsubishi.DepoID,
                DeliveryTimeClass = Mitsubishi.DeliveryTimeClass
            }).ToList();
        }

        /// <summary>
        /// 登録
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="targets"></param>
        /// <returns></returns>
        private static int InsertShipmentSchedules(SqlConnection conn, List<dynamic> targets)
        {
            if (!targets.Any())
                return 0;

            var sql = @"
                INSERT INTO D_ShipmentSchedule
                (
                    DepoID,
                    CompanyID,
                    DeliveryTimeClass,
                    DeliveryName,
                    DeliveryCode,
                    DeliveryFactoryKubun,
                    DeliverySlipNumber,
                    DeliveryProductName,
                    DeliveryProductNumber,
                    SupplierProductNumber,
                    Quantity,
                    NumberOfBoxes,
                    DeliveryFactoryName,
                    DeliveryDate,
                    DeliveryLocation,
                    LotQuantity,
                    IssuedDate,
                    CreatedAt,
                    CreatedBy,
                    UpdatedAt,
                    UpdatedBy
                )
                VALUES
                (
                    @DepoID,
                    @CompanyID,
                    @DeliveryTimeClass,
                    @DeliveryName,
                    @DeliveryCode,
                    @DeliveryFactoryKubun,
                    @DeliverySlipNumber,
                    @DeliveryProductName,
                    @DeliveryProductNumber,
                    @SupplierProductNumber,
                    @Quantity,
                    @NumberOfBoxes,
                    @DeliveryFactoryName,
                    @DeliveryDate,
                    @DeliveryLocation,
                    @LotQuantity,
                    @IssuedDate,
                    @CreatedAt,
                    @CreatedBy,
                    @UpdatedAt,
                    @UpdatedBy
                )
            ";

            return conn.Execute(sql, targets);
        }

        /// <summary>
        /// エラーログ
        /// </summary>
        /// <param name="ngList"></param>
        private static void OutputErrorLog(List<dynamic> ngList)
        {
            for (int i = 0; i < ngList.Count; i++)
            {
                var ng = ngList[i];

                Console.WriteLine($"{i + 1}. NG: 納品書番号 : {ng.DeliverySlipNumber}　エラー：{ng.ErrorMessage}");
            }
        }
    }
}
