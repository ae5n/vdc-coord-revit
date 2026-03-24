import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportSharedCoordinatesReportTool(server: McpServer) {
  server.tool(
    "export_shared_coordinates_report",
    "Export a CSV report of shared coordinate data (project base point and survey point) for the host model and loaded linked models.",
    {
      includeLinkedModels: z.boolean().optional()
        .describe("Whether to include coordinate data from loaded Revit link instances. Default: true."),
      precision: z.number().int().min(0).max(6).optional()
        .describe("Decimal precision for coordinate values in feet. Default: 3."),
      anglePrecision: z.number().int().min(0).max(6).optional()
        .describe("Decimal precision for angular values in degrees. Default: 4."),
      maxPreviewRows: z.number().int().min(0).max(20).optional()
        .describe("Number of records to include in the response preview. Default: 5."),
      outputPath: z.string()
        .describe("Full path for the output CSV file. Always ask the user where to save before calling."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_shared_coordinates_report", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `export_shared_coordinates_report failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}
