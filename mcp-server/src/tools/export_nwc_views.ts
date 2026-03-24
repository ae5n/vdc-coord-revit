import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportNwcViewsTool(server: McpServer) {
  server.tool(
    "export_nwc_views",
    "Export 3D views from the active Revit model to Navisworks NWC files. This is a two-step tool: first call without viewNames to retrieve the list of available views, then present the list to the user and ask which views to export and where to save, then call again with viewNames and outputDirectory confirmed by the user.",
    {
      outputDirectory: z.string().optional()
        .describe("Directory to write NWC files. Must be confirmed with the user before exporting."),
      viewNames: z.array(z.string()).optional()
        .describe("3D view names to export. Omit on the first call to list available views. Must be confirmed with the user before exporting."),
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
