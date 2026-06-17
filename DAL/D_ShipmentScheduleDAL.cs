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
                using var conn = new SqlConnection(
                    ConnectToSQLServer.GetConnectionString("warehouse"));

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
                    Logger.Info($"取消処理開始 ShipmentScheduleID={target.ShipmentScheduleID}");

                    // LotNumber取得
                    var lotNumber = GetLotNumber(conn, tran, target.ShipmentScheduleID);

                    // 出荷レーン搬送削除
                    var deletedTransportCount = DeleteTransportShippingLaneResult(conn, tran, target);

                    // 搬送データが存在した場合のみロット順更新
                    if (deletedTransportCount > 0)
                        UpdateLotShipmentSequence(conn, tran, target, lotNumber);

                    // StoreOut削除
                    DeleteStoreOut(conn, tran, target, lotNumber);

                    // 関連データ削除
                    DeleteShipmentRelatedData(conn, tran, target.ShipmentScheduleID);

                    tran.Commit();

                    Logger.Info(
                        $"取消処理完了 ShipmentScheduleID={target.ShipmentScheduleID}");
                }
                catch (Exception ex)
                {
                    tran.Rollback();

                    string error =
                        $"ShipmentScheduleID={target.ShipmentScheduleID} : {ex.Message}";

                    errorList.Add(error);

                    Logger.Error(error);

                    Logger.Error(ex.ToString());
                }
            }

            return errorList;
        }

        /// <summary>
        /// 出荷指示の取消対象取得
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
        /// LotNumber取得
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="shipmentScheduleID"></param>
        /// <returns></returns>
        private static string GetLotNumber(
            SqlConnection conn,
            SqlTransaction tran,
            int shipmentScheduleID)
        {
            return conn.QueryFirstOrDefault<string>(
                @"
                SELECT TOP 1 LotNumber
                FROM D_PrepareShipmentResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID
                ",
                new
                {
                    ShipmentScheduleID = shipmentScheduleID
                },
                tran);
        }

        /// <summary>
        /// ロット順更新
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        /// <param name="lotNumber"></param>
        private static void UpdateLotShipmentSequence(
            SqlConnection conn,
            SqlTransaction tran,
            dynamic target,
            string lotNumber)
        {
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
        /// 出庫削除
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        /// <param name="lotNumber"></param>
        private static void DeleteStoreOut(
            SqlConnection conn,
            SqlTransaction tran,
            dynamic target,
            string lotNumber)
        {
            if (string.IsNullOrEmpty(lotNumber))
                return;

            conn.Execute(
                @"
                DELETE FROM D_StoreOut
                WHERE DeliveryDate = @DeliveryDate
                  AND DeliverySlipNumber = @DeliverySlipNumber
                  AND DeliveryProductNumber = @DeliveryProductNumber
                  AND SupplierProductNumber = @SupplierProductNumber
                  AND LotNumber = @LotNumber
                ",
                new
                {
                    target.DeliveryDate,
                    target.DeliverySlipNumber,
                    target.DeliveryProductNumber,
                    target.SupplierProductNumber,
                    LotNumber = lotNumber
                },
                tran);
        }

        /// <summary>
        /// 関連データ削除
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="shipmentScheduleID"></param>
        private static void DeleteShipmentRelatedData(
            SqlConnection conn,
            SqlTransaction tran,
            int shipmentScheduleID)
        {
            conn.Execute(
                @"
                DELETE FROM D_MatchKanbanResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID;

                DELETE FROM D_InspectProductResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID;

                DELETE FROM D_PrepareShipmentResult
                WHERE ShipmentScheduleID = @ShipmentScheduleID;

                DELETE FROM D_ShipmentSchedule
                WHERE ShipmentScheduleID = @ShipmentScheduleID;

                DELETE FROM D_Shipment
                WHERE ShipmentScheduleID = @ShipmentScheduleID;
                ",
                new
                {
                    ShipmentScheduleID = shipmentScheduleID
                },
                tran);
        }

        /// <summary>
        /// 出荷レーン搬送削除
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="tran"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        private static int DeleteTransportShippingLaneResult(
            SqlConnection conn,
            SqlTransaction tran,
            dynamic target)
        {
            return conn.Execute(
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