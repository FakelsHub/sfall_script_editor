﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using ICSharpCode.TextEditor.Document;

namespace ScriptEditor.CodeTranslation
{
    /// <summary>
    /// SSL code folding strategy for ICSharpCode.TextEditor.
    /// </summary>
    public class CodeFolder : IFoldingStrategy
    {
        public List<FoldMarker> GenerateFoldMarkers(IDocument document, string fileName, object parseInformation)
        {
            ProgramInfo pi = (ProgramInfo)parseInformation;

            List<FoldMarker> list = new List<FoldMarker>(pi.procs.Length);
            int minStart = -1;
            fileName = fileName.ToLowerInvariant();
            for (int i = 0; i < pi.procs.Length; i++) {
                if (pi.procs[i].filename != fileName || pi.procs[i].d.start >= pi.procs[i].d.end)
                    continue;
                int dstart = pi.procs[i].d.start - 1;
                if (minStart > dstart || minStart == -1)
                    minStart = dstart;
                int len = document.GetLineSegment(pi.procs[i].d.end - 1).Length;
                list.Add(new FoldMarker(document, dstart, 0, pi.procs[i].d.end - 1, len, FoldType.MemberBody, " " + pi.procs[i].name.ToUpperInvariant() + " "));
            }

            if (list.Count > 0 && Path.GetExtension(fileName) == ".ssl") {
                ProcBlock dRegion = Parser.GetRegionDeclaration(document.TextContent, minStart);
                if (dRegion.end < 0)
                    dRegion.end = minStart - 2;
                if (dRegion.end > dRegion.begin)
                    list.Add(new FoldMarker(document, dRegion.begin, 0, dRegion.end, 1000, FoldType.Region, " - Declaration Region - "));
            }

            List<ProcBlock> blockList = Parser.GetDeclarationVariableBlock(document.TextContent);
            foreach (ProcBlock block in blockList)
                list.Add(new FoldMarker(document, block.begin, 0, block.end, 1000, FoldType.TypeBody, " - Variables - "));

            return list;
        }
    }   
}