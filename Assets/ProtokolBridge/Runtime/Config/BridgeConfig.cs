using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProtokolBridge
{
    [Serializable]
    public class BridgeConfig
    {
        public List<EndpointConfig> endpoints = new List<EndpointConfig>();
        public List<RouteRule> rules = new List<RouteRule>();
        public int logCapacity = 500;

        /// <summary>
        /// Frames per second the app renders at. This is a bridge, not a game: the
        /// panel changes about four times a second, so rendering at the monitor's
        /// refresh rate burns a core and a GPU for nothing on a machine that runs
        /// for weeks.
        ///
        /// It does NOT affect how fast messages are received — transports run on
        /// their own threads. It does set the resolution of rule delays, which are
        /// flushed once per frame, so going below ~30 makes short delays coarse.
        /// 0 means uncapped.
        /// </summary>
        public int targetFrameRate = 60;

        /// <summary>A small, self-explanatory starting point for a first run.</summary>
        public static BridgeConfig CreateDefault()
        {
            var serial = new EndpointConfig
            {
                name = "Arduino",
                kind = TransportKind.Serial,
                portName = "COM3",
                baudRate = 115200,
                useLineFraming = true,
                autoStart = false
            };

            var osc = new EndpointConfig
            {
                name = "OSC Out",
                kind = TransportKind.Osc,
                listenPort = 8000,
                remoteHost = "127.0.0.1",
                remotePort = 9000,
                autoStart = false
            };

            var config = new BridgeConfig();
            config.endpoints.Add(serial);
            config.endpoints.Add(osc);

            var rule = new RouteRule
            {
                name = "Serial -> OSC",
                sourceEndpointId = serial.id,
                matchMode = MatchMode.Any,
                fieldSeparator = ","
            };

            var toOsc = new RuleOutput
            {
                targetEndpointId = osc.id,
                outputMode = OutputMode.Template,
                outputAddress = "/protokol/value"
            };
            toOsc.outputArgs.Add(new OutputArg(ArgType.Auto, "{field0}"));
            rule.outputs.Add(toOsc);

            config.rules.Add(rule);

            return config;
        }
    }

    /// <summary>
    /// Reads and writes the config as JSON next to the build, so an operator can
    /// edit or copy it between machines without opening Unity.
    /// </summary>
    public static class ConfigStore
    {
        public const string FileName = "protokol-bridge.json";

        public static string DefaultPath
        {
            get { return Path.Combine(Application.persistentDataPath, FileName); }
        }

        public static bool TryLoad(string path, out BridgeConfig config, out string error)
        {
            config = null;
            error = null;

            try
            {
                if (!File.Exists(path))
                {
                    error = "No config at " + path;
                    return false;
                }

                var json = File.ReadAllText(path);
                config = JsonUtility.FromJson<BridgeConfig>(json);

                if (config == null)
                {
                    error = "Config file could not be parsed";
                    return false;
                }

                // JsonUtility leaves null lists when a key is absent.
                if (config.endpoints == null) config.endpoints = new List<EndpointConfig>();
                if (config.rules == null) config.rules = new List<RouteRule>();

                // Folds single-output rules written by older builds into the
                // outputs list, and fills in any null collections.
                foreach (var rule in config.rules) rule.MigrateLegacy();

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TrySave(string path, BridgeConfig config, out string error)
        {
            error = null;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(path, JsonUtility.ToJson(config, true));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
