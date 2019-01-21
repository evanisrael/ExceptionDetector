﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ExceptionDetectorUpdated
{
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
	public class ExceptionDetectorUpdated : MonoBehaviour
	{
		//===Exception storage===
		//Key: Class name, Value: StackInfo
		private static Dictionary<string, StackInfo> classCache = new Dictionary<string, StackInfo>();
		//Key: DLL name, Value: [Key: Class.Method name, Value: Number of throws]
		private static Dictionary<string, Dictionary<StackInfo, int>> methodThrows = new Dictionary<string, Dictionary<StackInfo, int>>();
		//Time of all the throws
		private static Queue<float> throwTime = new Queue<float>();
		//===Display state===
		private float lastDisplayTime = float.NegativeInfinity;
		private string displayState;
		private Rect windowRect = new Rect(10, 10, 400, 50);
		private GUILayoutOption[] expandOptions = null;
		private static HashSet<string> kspDlls = new HashSet<string>();
		private static HashSet<string> unityDlls = new HashSet<string>();
		private HashSet<string> textureErrors = new HashSet<string>();

		private static int stackLogTick = 0;
		private static string preStack = String.Empty;

		//private static readonly String PARTLOADER = "PartLoader:";
		//private static readonly String TEXTURELOADER = "Load(Texture):";
		//private static readonly String MODELLOADER = "Load(Model):";
		//private static readonly String AUDIOLOADER = "Load(Audio):";
		//private static readonly String MODMAN = "[MODULEMANAGER]";
		//private static readonly String TOOLBAR = "ToolbarControl:";
		//private static readonly String CONTRACTTYPE = "ContractConfigurator.ContractType:";
		//private static readonly String CONTRACTCONFIGTYPE = "ContractConfigurator.ContractConfigurator:";
		//private static readonly String CONTRACTGROUPTYPE = "ContractConfigurator.ContractGroup:";
		//private static readonly String CONTRACTAGENTTYPE = "[Agent]:";
		//private static readonly String CONTRACTORBITTYPE = "ContractConfigurator.OrbitFactory:";
		//private static readonly String KOSTYPE = "kOS:";
		//private static readonly String FILTEREXTTYPE = "[Filter Extensions 3.2.0.3]:";

		internal static Dictionary<string, string> SinglePassValues = new Dictionary<string, string>();
		internal static Dictionary<string, string> DoublePassValues = new Dictionary<string, string>();
		private static Dictionary<string, HashSet<string>> _errors = new Dictionary<string, HashSet<string>>();
		private static Dictionary<string, ulong> _exceptionCount = new Dictionary<string, ulong>();
		private static string prvConditionStatement = String.Empty;
		
		private static readonly string _assemblyPath = Path.GetDirectoryName(typeof(ExceptionDetectorUpdated).Assembly.Location);
		internal static String SettingsFile { get; } = Path.Combine(_assemblyPath, "settings.cfg");
		internal static String LogFile { get; } = Path.Combine(_assemblyPath, "Log/edu.log");

		public static bool FullLog { get; set; } = false;
		public static bool HideKnowns { get; set; } = false;
		public static bool ShowInfoMessage { get; set; } = false;

		public void Awake()
		{
			InitLog();
			Config.Load();
			DontDestroyOnLoad(this);
			Application.logMessageReceivedThreaded += HandleLogEntry;
			GUILayoutOption[] expandOptions = new GUILayoutOption[2];
			expandOptions[0] = GUILayout.ExpandWidth(true);
			expandOptions[1] = GUILayout.ExpandHeight(true);
			kspDlls.Add("assembly-csharp-firstpass");
			kspDlls.Add("assembly-csharp");
			kspDlls.Add("kspassets.dll");
			//kspDlls.Add("kspcore.dll");
			// kspDlls.Add("ksputil.dll");
			unityDlls.Add("unityengine.dll");
			unityDlls.Add("unityengine.networking.dll");
			unityDlls.Add("unityengine.ui.dll");
		}

		private void UpdateDisplayString()
		{
			if ((Time.realtimeSinceStartup - lastDisplayTime) > 0.2f)
			{
				lastDisplayTime = Time.realtimeSinceStartup;
				if (throwTime.Count > 0)
				{
					//10 second average sampling
					while (throwTime.Count > 0 && (throwTime.Peek() < (Time.realtimeSinceStartup - 10f)))
					{
						throwTime.Dequeue();
					}
					StringBuilder sb = new StringBuilder();
					sb.Append("Throws per second: ");
					sb.Append(throwTime.Count / 10f);
					sb.AppendLine(" TPS.");
					foreach (KeyValuePair<string, Dictionary<StackInfo, int>> dllEntry in methodThrows)
					{
						sb.AppendLine(dllEntry.Key);
						foreach (KeyValuePair<StackInfo, int> methodThrowEntry in dllEntry.Value)
						{
							sb.Append("    ");
							if (methodThrowEntry.Key.namespaceName != null)
							{
								sb.Append(methodThrowEntry.Key.namespaceName);
								sb.Append(".");
							}
							sb.Append(methodThrowEntry.Key.className);
							sb.Append(".");
							sb.Append(methodThrowEntry.Key.methodName);
							sb.Append(": ");
							sb.AppendLine(methodThrowEntry.Value.ToString());
						}
					}
					displayState = sb.ToString();
				}
				else
				{
					displayState = null;
				}
			}
		}

		public void OnGUI()
		{
			//Update the state string every 0.2s so we can read it.
			if (Event.current.type == EventType.Layout)
			{
				UpdateDisplayString();
			}
			if (displayState != null)
			{
				//Random number
				windowRect = GUILayout.Window(1660952404, windowRect, DrawMethod, "Exception Detector", expandOptions);
			}
		}

		private void DrawMethod(int windowID)
		{
			GUI.DragWindow();
			GUILayout.Label(displayState);
		}

		private static void logTheMessage(string condition, string stackTrace, LogType logType)
		{
			if (ExceptionDetectorUpdated.FullLog)
			{
				if (condition != "\n") WriteLog("Condition:\t" + condition);
				if (stackTrace != "\n") WriteLog("StackTrace:\t" + stackTrace + "\nLogType:\t" + logType);
			}
		}

		private static void logTheMessage(string condition, string stackTrace, String name)
		{
			WriteLog("Condition:\t" + condition);
			WriteLog("StackTrace:\t" + stackTrace);
			WriteLog("\nLogType:\t" + name);
		}

		public static void HandleLogEntry(string condition, string stackTrace, LogType logType)
		{
			//bool texType = condition.Contains(TEXTURELOADER);
			//bool parType = condition.Contains(PARTLOADER);
			//bool modType = condition.Contains(MODELLOADER);
			//bool auType = condition.Contains(AUDIOLOADER);
			//bool mmType = condition.Contains(MODMAN);
			//bool toolType = condition.Contains(TOOLBAR);
			//bool contractType = condition.Contains(CONTRACTTYPE);

			//WriteLog("\n<<<<<<<< " + stackLogTick + "\t" + DoublePassValues.Count + "\t" + prvConditionStatement);
			string stkMsg = String.Empty;
			bool doublePass = CheckPass(condition, DoublePassValues);
			bool singlePass = false;
			if(!doublePass)
				singlePass = CheckPass(condition, SinglePassValues); 
				//condition.Contains(MODMAN) || condition.Contains(TOOLBAR) || condition.Contains(CONTRACTTYPE) || condition.Contains(CONTRACTAGENTTYPE) || condition.Contains(CONTRACTGROUPTYPE) || condition.Contains(KOSTYPE) || condition.Contains(CONTRACTCONFIGTYPE) || condition.Contains(FILTEREXTTYPE) || condition.Contains(CONTRACTORBITTYPE);

			if (doublePass)
			{
				// WriteLog(stackTrace);
				// save it
				preStack = condition;
				stackLogTick = 1;
				if (!HideKnowns)
				{
					//WriteLog("hidingknowns\n");
					logTheMessage(condition, stackTrace, logType);
				}
			}
			//else if (stackLogTick == 0 && !condition.Equals(prvConditionStatement))
			//{
			//	logTheMessage(condition, stackTrace, logType);
			//	WriteLog("\n");
			//}

			if (logType == LogType.Log && ShowInfoMessage)
			{
				logTheMessage(condition, stackTrace, logType);
			}
			if (logType == LogType.Error || logType == LogType.Warning)
			{
				//WriteLog(logType.ToString() + " : " + stackLogTick + " : " + condition);
				if (stackLogTick == 1)
				{
					prvConditionStatement = condition;

					condition = CleanCondition(condition);

					WriteLog("*EDU*\t" + preStack + "--> " + condition + "\n");
					stackLogTick = 0;
					preStack = String.Empty;
				}
				else if (singlePass)
				{
					WriteLog("*EDU*\t" + condition + "\n");
				}
				else if (stackLogTick == 0 && !condition.Equals(prvConditionStatement))
				{
					logTheMessage(condition, stackTrace, logType);
					WriteLog(stackTrace);
					WriteLog("\n");
				}

				// handle partloader errors
			}
			else if (logType == LogType.Exception)
			{
				if (_exceptionCount.ContainsKey(condition))
				{
					ulong count = _exceptionCount[condition];
					_exceptionCount[condition] = ++count;
					if (_exceptionCount[condition] > 20)
					{
						stkMsg = "Exception has been called " + count + " times";
					}
				}
				else
				{
					_exceptionCount.Add(condition, 1);
					stkMsg = stackTrace;
				}
				logTheMessage(condition, stkMsg, "**EDU-Exception");
				WriteLog("EDU-EXCEPTION****\n\n\n");
				using (StringReader sr = new StringReader(stackTrace))
				{
					StackInfo firstInfo = null;
					StackInfo stackInfo = null;
					bool foundMod = false;
					string currentLine = sr.ReadLine();
					while (!foundMod && currentLine != null)
					{
						stackInfo = GetStackInfo(currentLine);
						if (firstInfo == null)
						{
							firstInfo = stackInfo;
						}
						if (stackInfo.isMod)
						{
							//We found a mod in the trace, let's blame them.
							foundMod = true;
							break;
						}
						currentLine = sr.ReadLine();
					}
					if (!foundMod)
					{
						//If we didn't find a mod, blame the method that threw.
						stackInfo = firstInfo;
					}
					if (!methodThrows.ContainsKey(stackInfo.dllName))
					{
						methodThrows.Add(stackInfo.dllName, new Dictionary<StackInfo, int>());
					}
					if (!methodThrows[stackInfo.dllName].ContainsKey(stackInfo))
					{
						methodThrows[stackInfo.dllName].Add(stackInfo, 0);
					}
					methodThrows[stackInfo.dllName][stackInfo]++;
				}
				throwTime.Enqueue(Time.realtimeSinceStartup);
			}
			//WriteLog(">>>>>>>>\n");
		}

		private static bool CheckPass(string condition, Dictionary<string, string> passValues)
		{
			bool retVal = false;

			foreach (KeyValuePair<string, string> val in passValues)
			{
				if (condition.Contains(val.Value))
				{
					retVal = true;
					break;
				}
			}
			//if(condition.Contains(TEXTURELOADER) || condition.Contains(PARTLOADER) || condition.Contains(MODELLOADER) || condition.Contains(AUDIOLOADER);
			return retVal;
		}

		private static string CleanCondition(string condition)
		{
			string retVal = String.Empty;

			if (String.IsNullOrEmpty(condition))
			{
				retVal = "UNKNOWN ERROR";
			}
			else
			{
				string pathToGameData = Path.GetFullPath(Path.Combine(_assemblyPath, ".." + Path.DirectorySeparatorChar + ".. " + Path.DirectorySeparatorChar));
				retVal = condition.Replace(pathToGameData, String.Empty).Replace("GameData", String.Empty);
			}
			return retVal;
		}

		public static StackInfo GetStackInfo(string stackLine)
		{
			StackInfo retVal = new StackInfo();
			if (stackLine.StartsWith("UnityEngine.") || stackLine.StartsWith("KSPAssets."))
				return retVal;

			if (classCache.ContainsKey(stackLine))
			{
				return classCache[stackLine];
			}

			string processLine = null;
			try
			{
				processLine = stackLine.Substring(0, stackLine.LastIndexOf(" ("));
			}
			catch (ArgumentOutOfRangeException oor)
			{

			}

			string methodName = processLine.Substring(processLine.LastIndexOf(".") + 1);
			processLine = processLine.Substring(0, processLine.Length - (methodName.Length + 1));
			if (processLine.Contains("["))
			{
				processLine = processLine.Substring(0, processLine.IndexOf("["));
			}
			//UNITY WHY DO YOU HAVE TO BE SO BAD
			//Type foundType = Type.GetType(processLine);
			Type foundType = null;
			foreach (Assembly testAssembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (Type testType in testAssembly.GetExportedTypes())
				{
					if (testType.FullName == processLine)
					{
						foundType = testType;
						break;
					}
				}
				if (foundType != null)
				{
					break;
				}
			}
			if (foundType != null)
			{
				string dllPath = foundType.Assembly.Location;
				retVal.dllName = Path.GetFileNameWithoutExtension(dllPath);
				if (!dllPath.ToLower().Contains("gamedata"))
				{
					retVal.isMod = false;
					if (retVal.dllName.ToLower() == "mscorelib")
					{
						retVal.dllName = "Mono";
					}
					if (unityDlls.Contains(retVal.dllName.ToLower()))
					{
						retVal.dllName = "Unity";
					}
					if (kspDlls.Contains(retVal.dllName.ToLower()))
					{
						retVal.dllName = "KSP";
					}
				}
				retVal.namespaceName = foundType.Namespace;
				retVal.className = foundType.Name;
				if (retVal.className.Contains("`"))
				{
					retVal.className = retVal.className.Substring(0, retVal.className.IndexOf("`"));
				}
				retVal.methodName = methodName;
				classCache.Add(stackLine, retVal);
				return retVal;
			}
			StackInfo unknownVal = new StackInfo { dllName = "Unknown", className = processLine };

			if (unknownVal.className.Contains("`"))
			{
				unknownVal.className = unknownVal.className.Substring(0, unknownVal.className.IndexOf("`"));
			}

			unknownVal.methodName = methodName;
			classCache.Add(stackLine, unknownVal);
			return unknownVal;
		}

		public static void WriteLog(string strMessage)
		{
			if (!String.IsNullOrEmpty(strMessage))
			{
				FileStream objFilestream = new FileStream(ExceptionDetectorUpdated.LogFile, FileMode.Append, FileAccess.Write);
				StreamWriter objStreamWriter = new StreamWriter((Stream)objFilestream);
				objStreamWriter.AutoFlush = true;
				objStreamWriter.WriteLine(strMessage);
				objStreamWriter.Close();
				objFilestream.Close();
			}
		}

		private void InitLog()
		{
			FileStream objFilestream = new FileStream(ExceptionDetectorUpdated.LogFile, FileMode.Create, FileAccess.Write);
			StreamWriter objStreamWriter = new StreamWriter((Stream)objFilestream);
			objStreamWriter.AutoFlush = true;
			objStreamWriter.WriteLine(DateTime.Now);
			objStreamWriter.WriteLine(Path.GetFullPath(Path.Combine(_assemblyPath, ".." + Path.DirectorySeparatorChar + ".. " + Path.DirectorySeparatorChar)));
			objStreamWriter.WriteLine("\n\n");
			objStreamWriter.Close();
			objFilestream.Close();
		}
	}

	public class StackInfo
	{
		public bool isMod = true;
		public string dllName;
		public string namespaceName;
		public string className;
		public string methodName;
	}
}