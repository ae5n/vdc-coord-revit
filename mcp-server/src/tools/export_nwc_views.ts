import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportNwcViewsTool(server: McpServer) {
  server.tool(
    "export_nwc_views",
    "Export 3D views from the active Revit model to Navisworks NWC files. If no view names are specified, all non-template 3D views are exported.",
    {
      coordinates: z.enum(["Shared", "Project", "Internal"]).optional()
        .describe("Coordinate system used during export. Default: 'Shared'."),
      exportLinks: z.boolean().optional()
        .describe("Whether to convert linked models during export. Default: true."),
      divideFileIntoLevels: z.boolean().optional()
        .describe("Whether to divide the file into levels. Default: true."),
      exportElementIds: z.boolean().optional()
        .describe("Whether to include element IDs. Default: true."),
      outputDirectory: z.string().optional()
        .describe("Directory to write NWC files. If omitted, auto-generated in ~/Documents/RevitSuite/NWC/."),
      viewNames: z.array(z.string()).optional()
        .describe("Specific 3D view names to export. If omitted, all non-template 3D views are exported."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_nwc_views", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `export_nwc_views failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}
