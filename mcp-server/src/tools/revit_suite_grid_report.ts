import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerRevitSuiteGridReportTool(server: McpServer) {
  server.tool(
    "run_grid_report",
    "Export a CSV report of grid lines across the host model and any loaded linked models, including origin coordinates and angles.",
    {
      includeLinkedModels: z.boolean().optional()
        .describe("Whether to include grid information from linked Revit models. Default: true."),
      precision: z.number().int().min(0).max(6).optional()
        .describe("Decimal precision for coordinate and length values. Default: 2."),
      maxPreviewRows: z.number().int().min(0).max(20).optional()
        .describe("Number of rows to include in the response preview. Default: 5."),
      outputPath: z.string().optional()
        .describe("Full path for the output CSV file. If omitted, auto-generated in ~/Documents/RevitSuite/."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("run_grid_report", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `run_grid_report failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}
