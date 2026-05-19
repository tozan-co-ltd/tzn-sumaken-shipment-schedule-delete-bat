using System.Data.SqlClient;
using tzn_sumaken_shipment_schedule_delete_bat.DAL;

class Tzn_sumaken_shipment_schedule_delete_bat
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 出荷指示の取消バッチ
    /// </summary>
    static void Main()
    {
        // 二重起動を禁止
        // Mutexを作成する
        Mutex mutex = new(true, "tzn-sumaken-shipment-schedule-delete-bat", out bool createdNew);

        // ログ取得
        Logger.Info($@"出荷指示の取消バッチ開始");

        try
        {
            // Mutexを既に持っているプロセスがいる場合はエラー
            if (!createdNew)
            {
                // メッセージを表示して終了する
                Console.WriteLine("Another instance is already running.");
                return;
            }
            // SQL実行
            D_ShipmentScheduleDAL.DeleteShipmentScheduleFromEDI();
        }
        catch (SqlException ex)
        {
            // エラー時のログ取得
            Logger.Error($@"SQLエラー内容:{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            // エラー時のログ取得
            Logger.Error($@"エラー内容:{ex.Message}");
            return;
        }
        finally
        {
            // Mutexを解放する
            mutex.ReleaseMutex();
        }
        // ログ取得
        Logger.Info($@"出荷指示の取消バッチ終了 完了日時:{DateTime.Now}");

# if DEBUG
        Console.WriteLine("キーを押して下さい");
        Console.ReadKey(); // ユーザーの入力を待つ
# endif 
    }
}