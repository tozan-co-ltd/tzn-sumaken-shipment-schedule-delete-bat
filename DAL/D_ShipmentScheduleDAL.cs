using Dapper;
using NLog;
using System.Data.SqlClient;
using tzn_sumaken_shipment_schedule_delete_bat.Commons;

namespace tzn_sumaken_shipment_schedule_delete_bat.DAL
{
    internal class D_ShipmentScheduleDAL
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 出荷指示の取り消し
        /// </summary>
        public static void DeleteShipmentScheduleFromEDI()
        {
            try
            {
                using var conn = new SqlConnection(ConnectToSQLServer.GetConnectionString("warehouse"));

                conn.Open();

                // 取消データ処理
                var errors = ProcessCancelShipmentSchedule(conn);

                if (errors.Any())
                {
                    Console.WriteLine("取消処理でエラー発生");

                    foreach (var error in errors)
                    {
                        Console.WriteLine(error);
                    }
                }
                else
                {
                    Console.WriteLine("取消データ処理完了");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);

                throw;
            }
        }

        /// <summary>
        /// 出荷指示の取り消し
        /// </summary>
        /// <param name="conn"></param>
        /// <returns></returns>
        private static List<string> ProcessCancelShipmentSchedule(
        SqlConnection conn)
        {
            var errorList = new List<string>();

            var targets = GetCancelTargets(conn);

            Logger.Info($"取消対象件数={targets.Count}");

            if (!targets.Any())
                return errorList;

            foreach (var target in targets)
            {
                using var tran = conn.BeginTransaction();

                try
                {
                    Logger.Info(
                        $"取消処理開始 ShipmentScheduleID={target.ShipmentScheduleID}");

                    // TransportShippingLaneResultに存在確認
                    bool existsTransportResult =
                        ExistsTransportShippingLaneResult(conn, tran, target);

                    // ある場合LotShipmentSequence更新, DeleteTransportShippingLaneResult取消
                    if (existsTransportResult)
                    {
                        UpdateLotShipmentSequence(conn, tran, target);

                        DeleteTransportShippingLaneResult(conn, tran, target);
                    }

                    // 関連のデータを削除
                    DeleteShipmentRelatedData(conn, tran, target.ShipmentScheduleID);

                    tran.Commit();

                    Logger.Info(
                        $"取消処理完了 ShipmentScheduleID={target.ShipmentScheduleID}");
                }
                catch (Exception ex)
                {
                    tran.Rollback();

                    string error = $"ShipmentScheduleID={target.ShipmentScheduleID} : {ex.Message}";

                    errorList.Add(error);

                    Logger.Error(error);

                    Logger.Error(ex.ToString());
                }
            }

            return errorList;
        }

        /// <summary>
        /// 出荷指示の除対象を取得
        /// </summary>
        /// <param name="conn"></param>
        /// <returns></returns>
        private static List<dynamic> GetCancelTargets(SqlConnection conn)
        {
            var sql = @"
                SELECT DISTINCT ss.*
                FROM D_ShipmentSchedule ss

                INNER JOIN tozandbEDI.dbo.BU_VR_NohinshoTorikeshiData_d vr
                    ON  vr.VRNONO = ss.DeliverySlipNumber
                    AND vr.VRBUNO = ss.SupplierProductNumber
                    AND vr.VRSRYO = ss.Quantity
                    AND CONVERT(date, vr.VRDATE) = CONVERT(date, ss.DeliveryDate)

                WHERE vr.VRTRCD = 'J019'
                  AND vr.TorokuDateTime >= DATEADD(DAY, -2, GETDATE())
            ";

            return conn.Query<dynamic>(sql).ToList();
        }

        /// <summary>
        /// 出荷レーン搬送存在確認
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        private static bool ExistsTransportShippingLaneResult(
        SqlConnection conn,
        SqlTransaction tran,
        dynamic target)
        {
            var sql = @"
                SELECT COUNT(1)
                FROM D_TransportShippingLaneResult
                WHERE DeliveryCode = @DeliveryCode
                    AND DeliveryDate = @DeliveryDate
                    AND DeliverySlipNumber = @DeliverySlipNumber
            ";

            return conn.ExecuteScalar<int>(
                sql,
                new
                {
                    target.DeliveryCode,
                    target.DeliveryDate,
                    target.DeliverySlipNumber
                },
                tran) > 0;
        }

        /// <summary>
        /// ロット順更新、ロット追加
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        private static void UpdateLotShipmentSequence(
        SqlConnection conn,
        SqlTransaction tran,
        dynamic target)
        {
            // PrepareShipmentResultからLot取得
            var lotNumber = conn.QueryFirstOrDefault<string>(
                @"
                SELECT TOP 1 LotNumber
                FROM D_PrepareShipmentResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new
                {
                    target.ShipmentScheduleID
                },
                tran);

            if (string.IsNullOrEmpty(lotNumber))
                return;

            // 現在のsequenceを全部 +1
            conn.Execute(
                @"
                UPDATE D_LotShipmentSequence
                SET LotShipmentSequence = LotShipmentSequence + 1,
                    UpdatedAt = GETDATE()
                WHERE SupplierProductNumber = @SupplierProductNumber
                ",
                new
                {
                    target.SupplierProductNumber
                },
                tran);

            // 削除されていたLotを先頭に戻す
            conn.Execute(
                @"
                INSERT INTO D_LotShipmentSequence
                (
                    DepoID,
                    CompanyID,
                    SupplierProductNumber,
                    LotShipmentSequence,
                    LotNumber,
                    IsDeleted,
                    CreatedAt,
                    CreatedBy,
                    UpdatedAt,
                    UpdatedBy
                )
                VALUES
                (
                    @DepoID,
                    @CompanyID,
                    @SupplierProductNumber,
                    1,
                    @LotNumber,
                    0,
                    GETDATE(),
                    @UpdatedBy,
                    GETDATE(),
                    @UpdatedBy
                )
                ",
                new
                {
                    target.DepoID,
                    target.CompanyID,
                    target.SupplierProductNumber,
                    LotNumber = lotNumber,
                    target.UpdatedBy
                },
                tran);
        }

        /// <summary>
        /// 関連のデータを削除
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="shipmentScheduleID"></param>
        private static void DeleteShipmentRelatedData(
        SqlConnection conn,
        SqlTransaction tran,
        int shipmentScheduleID)
        {
            // MatchKanbanResult
            conn.Execute(
                @"
                DELETE FROM D_MatchKanbanResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new { ShipmentScheduleID = shipmentScheduleID },
                tran);

            // InspectProductResult
            conn.Execute(
                @"
                DELETE FROM D_InspectProductResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new { ShipmentScheduleID = shipmentScheduleID },
                tran);

            // PrepareShipmentResult
            conn.Execute(
                @"
                DELETE FROM D_PrepareShipmentResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new { ShipmentScheduleID = shipmentScheduleID },
                tran);

            // ShipmentSchedule
            conn.Execute(
                @"
                DELETE FROM D_ShipmentSchedule
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new { ShipmentScheduleID = shipmentScheduleID },
                tran);
        }

        /// <summary>
        /// 出荷レーン搬送削除
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        private static void DeleteTransportShippingLaneResult(
        SqlConnection conn,
        SqlTransaction tran,
        dynamic target)
        {
            conn.Execute(
                @"
                DELETE FROM D_TransportShippingLaneResult
                WHERE DeliveryCode = @DeliveryCode
                    AND DeliveryDate = @DeliveryDate
                    AND DeliverySlipNumber = @DeliverySlipNumber
                ",
                new
                {
                    target.DeliveryCode,
                    target.DeliveryDate,
                    target.DeliverySlipNumber
                },
                tran);
        }
    }
}
