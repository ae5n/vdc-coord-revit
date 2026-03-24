import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerRevitSuiteQaqcTool(server: McpServer) {
  server.tool(
    "run_qaqc",
    "Trigger the RevitSuite QA/QC workflow. Use 'export' to write model control points to a CSV template for field survey. Use 'import' to read a completed field survey CSV, calculate deviations, and place annotation tags.",
    {
      mode: z.enum(["export", "import"]).optional()
        .describe("Which QAQC stage to run: 'export' (write control points CSV) or 'import' (read survey CSV and place deviation tags). Default: 'export'."),
      csvPath: z.string().optional()
        .describe("For 'export': optional output path for the CSV template. For 'import': required path to the completed field survey CSV."),
      toleranceGreen: z.number().min(0).optional()
        .describe("Maximum deviation in feet for green (pass) status. Default from schema (0.01 ft ≈ 1/8\")."),
      toleranceYellow: z.number().min(0).optional()
        .describe("Maximum deviation in feet for yellow (warning) status. Default from schema (0.05 ft ≈ 5/8\")."),
      comparisonMethod: z.enum(["horizontal", "vertical", "total"]).optional()
        .describe("How to calculate deviation: 'horizontal' (E/N only), 'vertical' (elevation only), or 'total' (3D). Default: 'horizontal'."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("run_qaqc", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `run_qaqc failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}
