import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerRevitSuiteFootingZonesTool(server: McpServer) {
  server.tool(
    "run_footing_zones",
    "Create transparent 3D influence zones around structural foundations in the active Revit model. Zones are DirectShapes representing the soil influence volume below each footing.",
    {
      clearDepth: z.number().min(0).optional()
        .describe("Vertical depth of the influence zone below the footing in feet. Default: 5.0."),
      slopeRatio: z.number().min(0).optional()
        .describe("Horizontal-to-vertical slope ratio used to expand the zone footprint. Default: 1.0."),
      verticalOffset: z.number().optional()
        .describe("Offset above the bottom of the footing before expanding the zone, in feet. Default: 0.0."),
      transparency: z.number().int().min(0).max(100).optional()
        .describe("Transparency percentage applied to the generated DirectShape geometry. Default: 50."),
      includeFootings: z.boolean().optional()
        .describe("Whether to automatically include all structural foundations. Default: true."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("run_footing_zones", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `run_footing_zones failed: ${error instanceof Error ? error.message : String(error)}` }] };
      }
    }
  );
}
