import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

function registerSharedCoordinatesReportTool(server: McpServer, toolName: string, description: string) {
  server.tool(
    toolName,
    description,
    {
      includeLinkedModels: z.boolean().optional()
        .describe("Whether to include coordinate data from loaded Revit link instances. Default: true."),
      precision: z.number().int().min(0).max(6).optional()
        .describe("Decimal precision for coordinate values in feet. Default: 3."),
      anglePrecision: z.number().int().min(0).max(6).optional()
        .describe("Decimal precision for angular values in degrees. Default: 4."),
      maxPreviewRows: z.number().int().min(0).max(20).optional()
        .describe("Legacy preview option. Ignored by the in-memory report response."),
      outputPath: z.string().optional()
        .describe("Optional full path for exporting the CSV/HTML report. Omit to return HTML report data only."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_shared_coordinates_report", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `${toolName} failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}

export function registerGetSharedCoordinatesReportTool(server: McpServer) {
  registerSharedCoordinatesReportTool(
    server,
    "get_shared_coordinates_report",
    "Get shared coordinates report data from the active Revit project and linked models. Returns HTML report content directly; exports files only when an output path is provided."
  );
}
