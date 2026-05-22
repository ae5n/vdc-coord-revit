// Copyright (c) 2026 sparx-fire (https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
// Licensed under the MIT License.

import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerPickLinkedRevitElementsTool(server: McpServer) {
  server.tool(
    "pick_linked_revit_elements",
    "Prompt the user in Revit to pick elements inside linked models and return structured host link and linked element metadata. Supports single or multiple selection.",
    {
      multiple: z
        .boolean()
        .default(true)
        .describe("Whether to allow multiple linked elements to be selected before finishing the picker."),
      prompt: z
        .string()
        .optional()
        .describe("Optional prompt shown in the Revit status bar while selecting linked elements."),
    },
    async (args) => {
      const params = {
        multiple: args.multiple,
        prompt: args.prompt,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("pick_linked_revit_elements", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `pick linked Revit elements failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
