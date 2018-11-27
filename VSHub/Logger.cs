using log4net;
using log4net.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSHub
{
    public static class Logger
    {
        public static void InitLogger()
        {
            XmlConfigurator.Configure();

            Log = LogManager.GetLogger("VSServer");
        }

        private static ILog Log;

        public static void Warning(object message)
        {
            Log.Warn(message);
        }

        public static void Debug(object message)
        {
            Log.Debug(message);
        }

        public static void Info(object message)
        {
            Log.Info(message);
        }

        public static void Error(object message)
        {
            Log.Error(message);
        }

        public static void Error(object message, Exception error)
        {
            Log.Error(message, error);
        }
    }
}
