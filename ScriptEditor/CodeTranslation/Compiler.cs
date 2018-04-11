﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

using ScriptEditor.TextEditorUI;

namespace ScriptEditor.CodeTranslation
{
    /// <summary>
    /// Class for compiling and decompile SSL code. Interacts with SSLC compiler via command line (EXE version) and DLL imports.
    /// </summary>
    public class Compiler
    {
        const string bakupFile = @"\BakupINT.tmp";

        private static readonly string decompilationPath = Path.Combine(Settings.scriptTempPath, "decomp.ssl");
        private static readonly string preprocessPath = Path.Combine(Settings.scriptTempPath, "preprocess.ssl");

        private string outputSSL;

        private string OverrideIncludeSSLCompile(string file)
        { 
            string[] text = File.ReadAllLines(file, (Settings.saveScriptUTF8) ? Encoding.UTF8 : Encoding.Default);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i].TrimStart().ToLower().StartsWith(Parser.INCLUDE)) {
                    string[] str = text[i].Split('"');
                    if (str.Length < 2)
                        continue;
                    if (str[1].IndexOfAny(Path.GetInvalidPathChars()) != -1)
                        continue;

                    bool overrides = false;
                    // для внешних препроцессоров переопределять только неотносительные пути
                    if (Settings.useMcpp || Settings.useWatcom || Settings.userCmdCompile)
                        overrides = Parser.OverrideIncludePath(ref str[1]);
                    else
                        overrides = Parser.OverrideIncludePath(ref str[1], file);
                    
                    if (overrides)
                        text[i]= str[0] + '"' + str[1] + '"';
                }
            }
            string cfile = Settings.scriptTempPath + '\\' + Path.GetFileName(file);
            File.WriteAllLines(cfile, text, (Settings.saveScriptUTF8) ? new UTF8Encoding(false) : Encoding.Default);
            return cfile;
        }

        public static string GetPreprocessedFile(string sName)
        {
            if (!File.Exists(preprocessPath))
                return null;
            
            sName = Path.Combine(Settings.scriptTempPath, Path.GetFileNameWithoutExtension(sName) + "_[preproc].ssl");
            File.Delete(sName);
            File.Move(preprocessPath, sName);

            return sName;   
        }

        public string GetOutputPath(string infile, string sourceDir = "")
        { 
            string outputFile = Path.GetFileNameWithoutExtension(infile);
            if (sourceDir.Length != 0 && (Settings.useMcpp || Settings.useWatcom))
                outputFile = outputFile.Remove(outputFile.Length - 6);
            
            outputFile = outputFile + ".int";

            if (Settings.ignoreCompPath && sourceDir.Length == 0)
                sourceDir = Path.GetDirectoryName(infile);

            outputSSL = (Settings.ignoreCompPath) 
                         ? Path.Combine(sourceDir, outputFile) 
                         : Path.Combine(Settings.outputDir, outputFile);
            
            return outputSSL;
        }

#if DLL_COMPILER
        public static string[] GetSslcCommandLine(string infile, bool preprocess) {
            return new string[] {
                "--", "-q",
                Settings.preprocess?"-P":"-p",
                Settings.optimize?"-O":"--",
                Settings.showWarnings?"--":"-n ",
                Settings.showDebug?"-d":"--",
                "-l", /* no logo */
                Path.GetFileName(infile),
                "-o",
                preprocess?preprocessPath:GetOutputPath(infile),
                null
            };
        }
#else
        private string GetSslcCommandLine(string infile, bool preprocess, string sourceDir, bool shortCircuit)
        {
            // неиспользовать препроцессор компилятора, если используется внешнний mcpp/wcc
            string usePreprocess = string.Empty;
            if (!Settings.useMcpp && !Settings.useWatcom)
                usePreprocess = preprocess ? "-P " : "-p ";
            
            return (usePreprocess)
                + ("-O" + Settings.optimize + " ")
                + (Settings.showWarnings ? "" : "-n ")
                + (Settings.showDebug ? "-d " : "")
                + ("-l ") /* always no logo */
                + ((Settings.shortCircuit || shortCircuit) ? "-s " : "")
                + "\"" + Path.GetFileName(infile) + "\" -o \"" + (preprocess ? preprocessPath : GetOutputPath(infile, sourceDir)) + "\"";
        }

        private string GetCommandLine(string infile, string outfile, string sourceDir, bool preprocess) {
            string prymaryPath = " ..", secondPath  = " ..";
            if (Settings.overrideIncludesPath && Settings.pathHeadersFiles != null) {
                prymaryPath = " \"" + sourceDir + "\"";
                secondPath  = " \"" + Settings.pathHeadersFiles + "\"" ;
            }

            return (Settings.useWatcom)
                    ? /* wcc command line */
                    ("\"" + infile + "\" ..\\scrTemp\\" + outfile
                     + ((preprocess) ? " c" : " l")
                     + prymaryPath + secondPath
                     + ((Settings.preprocDef != null) ? " -d" + Settings.preprocDef : string.Empty))
                    : /* mcpp command line */
                    ("\"" + infile + "\" ..\\scrTemp\\" + outfile
                     + ((Settings.showWarnings) ? " 1" : " 0")
                     + prymaryPath + secondPath
                     + ((Settings.preprocDef != null) ? (" -D" + Settings.preprocDef) : string.Empty)
                     + ((preprocess) ? " -P" : string.Empty));
        }

        private string GetCommandLine(string infile, bool shortCircuit) { 
            return ("\"" + infile + "\" "
                    + ((Settings.pathHeadersFiles != null) ? Settings.pathHeadersFiles : "..\\")
                    + " -d" + (Settings.preprocDef ?? string.Empty) 
                    + ((Settings.shortCircuit || shortCircuit) ? " -s" : string.Empty));
        }
#endif

#if DLL_COMPILER
        [System.Runtime.InteropServices.DllImport("resources\\sslc.dll")]
        private static extern int compile_main(int argc, string[] argv);

        [System.Runtime.InteropServices.DllImport("resources\\sslc.dll")]
        private static extern IntPtr FetchBuffer();
#endif

        public bool Compile(string infile, out string output, List<Error> errors, bool preprocessOnly, bool shortCircuit = false)
        {
            if (errors != null)
                errors.Clear();
            if (infile == null) {
                output = "No filename specified";
                return false;
            }
            bool success = false;
            string batPath = null;
            infile = Path.GetFullPath(infile);
            string srcfile = infile;
            string sourceDir = Path.GetDirectoryName(infile);

            if (Settings.overrideIncludesPath && Settings.pathHeadersFiles != null) {
                infile = OverrideIncludeSSLCompile(infile);
            }

            output = "****** " + DateTime.Now.ToString("HH:mm:ss") + " ******\r\n" + new String('-', 22);
            if (Settings.userCmdCompile && !preprocessOnly) {
                batPath = Path.Combine(Settings.ResourcesFolder, "usercomp.bat");
                ProcessStartInfo upsi = new ProcessStartInfo(batPath, GetCommandLine(infile, shortCircuit));
                success = RunProcess(upsi, Settings.ResourcesFolder, ref output);
            } else {
                // use external preprocessor
                string outfile = "preprocess.ssl"; //common preprocess file
                if (Settings.useMcpp || Settings.useWatcom) {
                    output += Environment.NewLine + (Settings.useWatcom ? "Open Watcom C32 preprocessing script: " : "External MCPP preprocessing script: ");
                    output += Path.GetFileName(infile) + Environment.NewLine;
                    output += "Predefine: " + (Settings.preprocDef ?? string.Empty) + Environment.NewLine;
                    
                    batPath = Path.Combine(Settings.ResourcesFolder, Settings.useWatcom ? "wcc.bat" : "mcpp.bat");
                    ProcessStartInfo ppsi = new ProcessStartInfo(batPath, GetCommandLine(infile, outfile, sourceDir, preprocessOnly));
                    success = RunProcess(ppsi, Settings.ResourcesFolder, ref output);
                    
                    output += new string('-', 22) + Environment.NewLine;
                    if (success) {
                        output += "Created preprocessing file: OK\r\n";
                        output += "[Done] Preprocessing script successfully completed.\r\n";
                    } else
                        output += "[Error] Preprocessing script failed...";
                }

                if (batPath != null) {
                    if (!success || preprocessOnly)
                        return success;

                    infile = Path.Combine(Settings.scriptTempPath, Path.GetFileNameWithoutExtension(infile) + "_[pre].ssl");
                    File.Delete(infile);
                    File.Move(Path.Combine(Settings.scriptTempPath, outfile), infile);
                }

#if DLL_COMPILER
                string origpath=Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(Path.GetDirectoryName(infile));
                string[] args=Settings.GetSslcCommandLine(infile, preprocessOnly);
                bool success=compile_main(args.Length, args)==0;
                output=System.Runtime.InteropServices.Marshal.PtrToStringAnsi(FetchBuffer());
                Directory.SetCurrentDirectory(origpath);
#else
                var exePath = Path.Combine(Settings.ResourcesFolder, "compile.exe");
                ProcessStartInfo psi = new ProcessStartInfo(exePath, GetSslcCommandLine(infile, preprocessOnly, sourceDir, shortCircuit));
                
                string bakupPath = Settings.scriptTempPath + bakupFile;
                if (File.Exists(outputSSL))
                    File.Copy(outputSSL, bakupPath, true);

                success = RunProcess(psi, Path.GetDirectoryName(infile), ref output);

                if (success)
                    File.Delete(bakupPath);
                else if (File.Exists(bakupPath))
                    File.Move(bakupPath, outputSSL);
#endif
            }
            if (errors != null && !Settings.userCmdCompile) 
                Error.BuildLog(errors, output, srcfile); //(Settings.useWatcom) ? infile : 
            if (Settings.overrideIncludesPath) 
                File.Delete(Settings.scriptTempPath + '\\' + Path.GetFileName(srcfile));

#if DLL_COMPILER
            output=output.Replace("\n", "\r\n");
#endif
            return success;
        }

        private bool RunProcess(ProcessStartInfo psi, string wDir, ref string output)
        {
            bool success;
            psi.RedirectStandardOutput = true;
            //psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = wDir;
            Process wp = Process.Start(psi);
            output += /*wp.StandardError.ReadToEnd() +*/ Environment.NewLine;
            output += wp.StandardOutput.ReadToEnd();
            wp.WaitForExit(1000);
            if (Settings.useMcpp || Settings.useWatcom)
                output += GetErrorLog();
            success = wp.ExitCode == 0;
            wp.Dispose();

            return success;
        }

        private string GetErrorLog()
        {
            string err = null;
            string file = Path.Combine(Settings.ResourcesFolder, Settings.useMcpp ? "mcpp.err" : "wcc.err");
            if (File.Exists(file)) {
                err = File.ReadAllText(file);
                File.Delete(file);
            }

            return err;
        }

        public string Decompile(string infile)
        {
            List<string> program = new List<string>{ "int2ssl.exe", "int2ssl_v35.exe" };
            if (Settings.oldDecompile)
                program.RemoveAt(0);

            foreach (string exe in program) 
            {
                var exePath = Path.Combine(Settings.ResourcesFolder, exe);
                ProcessStartInfo psi = new ProcessStartInfo(exePath, (Settings.decompileF1 ? "-1": String.Empty) 
                                                            + (Settings.tabsToSpaces ? " -s" + Settings.tabSize : String.Empty)
                                                            + " \"" + infile + "\" \"" + decompilationPath + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                p.WaitForExit(2000);
                if (!p.HasExited)
                    return null;
                if (p.ExitCode == 0)
                    break;
                p.Dispose();
            }
            if (!File.Exists(decompilationPath)) {
                return null;
            }
            SaveFileDialog sfDecomp = new SaveFileDialog();
            sfDecomp.Title = "Enter name to save decompile file";
            sfDecomp.Filter = "Script files|*.ssl";
            sfDecomp.RestoreDirectory = true;
            sfDecomp.InitialDirectory = Path.GetDirectoryName(infile);
            sfDecomp.FileName = Path.GetFileNameWithoutExtension(infile);
            string result;
            if (sfDecomp.ShowDialog() == DialogResult.OK)
                result = sfDecomp.FileName;
            else
                result = Path.Combine(Settings.scriptTempPath, Path.GetFileNameWithoutExtension(infile) + "_[decomp].ssl");
            sfDecomp.Dispose();
            File.Delete(result);
            File.Move(decompilationPath, result);
            return result;
        }
    }
}
