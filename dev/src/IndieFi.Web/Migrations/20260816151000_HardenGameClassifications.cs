using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IndieFi.Web.Migrations
{
    /// <inheritdoc />
    public partial class HardenGameClassifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_game_tags_active_sort",
                table: "game_tags",
                columns: new[] { "sort_order", "code" },
                filter: "is_active = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_game_tags_code",
                table: "game_tags",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_tags_display_name",
                table: "game_tags",
                column: "display_name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_tags_code_format",
                table: "game_tags",
                sql: "code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_tags_display_name_format",
                table: "game_tags",
                sql: "length(btrim(display_name)) > 0 AND display_name = btrim(display_name)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_tags_sort_order_range",
                table: "game_tags",
                sql: "sort_order >= 0 AND sort_order <= 10000");

            migrationBuilder.CreateIndex(
                name: "IX_game_platforms_active_sort",
                table: "game_platforms",
                columns: new[] { "sort_order", "code" },
                filter: "is_active = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_game_platforms_code",
                table: "game_platforms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_platforms_display_name",
                table: "game_platforms",
                column: "display_name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_platforms_code_format",
                table: "game_platforms",
                sql: "code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_platforms_display_name_format",
                table: "game_platforms",
                sql: "length(btrim(display_name)) > 0 AND display_name = btrim(display_name)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_platforms_sort_order_range",
                table: "game_platforms",
                sql: "sort_order >= 0 AND sort_order <= 10000");

            migrationBuilder.CreateIndex(
                name: "IX_game_genres_active_sort",
                table: "game_genres",
                columns: new[] { "sort_order", "code" },
                filter: "is_active = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_game_genres_code",
                table: "game_genres",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_genres_display_name",
                table: "game_genres",
                column: "display_name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_genres_code_format",
                table: "game_genres",
                sql: "code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_genres_display_name_format",
                table: "game_genres",
                sql: "length(btrim(display_name)) > 0 AND display_name = btrim(display_name)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_game_genres_sort_order_range",
                table: "game_genres",
                sql: "sort_order >= 0 AND sort_order <= 10000");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION enforce_game_tag_assignment_limit()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, public
                AS $$
                DECLARE
                    assignment_count integer;
                BEGIN
                    -- Serialize assignment changes for one game so two concurrent
                    -- inserts cannot both pass the count check.
                    PERFORM 1
                      FROM public.games
                     WHERE id = NEW.game_id
                       FOR UPDATE;

                    IF TG_OP = 'UPDATE' AND OLD.game_id = NEW.game_id THEN
                        RETURN NEW;
                    END IF;

                    SELECT count(*)
                      INTO assignment_count
                      FROM public.game_tag_assignments
                     WHERE game_id = NEW.game_id;

                    IF assignment_count >= 8 THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'CK_game_tag_assignments_max_per_game',
                            MESSAGE = 'A game cannot have more than 8 tags.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER game_tag_assignments_enforce_limit
                BEFORE INSERT OR UPDATE OF game_id ON game_tag_assignments
                FOR EACH ROW
                EXECUTE FUNCTION enforce_game_tag_assignment_limit();

                CREATE OR REPLACE FUNCTION enforce_game_platform_assignment_limit()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, public
                AS $$
                DECLARE
                    assignment_count integer;
                BEGIN
                    PERFORM 1
                      FROM public.games
                     WHERE id = NEW.game_id
                       FOR UPDATE;

                    IF TG_OP = 'UPDATE' AND OLD.game_id = NEW.game_id THEN
                        RETURN NEW;
                    END IF;

                    SELECT count(*)
                      INTO assignment_count
                      FROM public.game_platform_assignments
                     WHERE game_id = NEW.game_id;

                    IF assignment_count >= 12 THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '23514',
                            CONSTRAINT = 'CK_game_platform_assignments_max_per_game',
                            MESSAGE = 'A game cannot have more than 12 platforms.';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER game_platform_assignments_enforce_limit
                BEFORE INSERT OR UPDATE OF game_id ON game_platform_assignments
                FOR EACH ROW
                EXECUTE FUNCTION enforce_game_platform_assignment_limit();

                REVOKE ALL ON FUNCTION enforce_game_tag_assignment_limit() FROM PUBLIC;
                REVOKE ALL ON FUNCTION enforce_game_platform_assignment_limit() FROM PUBLIC;

                -- Catalog rows are migration-owned reference data. The web role may
                -- read them, while game relations remain intentionally mutable.
                REVOKE ALL ON game_genres, game_tags, game_platforms FROM indiefi_app;
                GRANT SELECT ON game_genres, game_tags, game_platforms TO indiefi_app;

                REVOKE ALL ON game_tag_assignments, game_platform_assignments FROM indiefi_app;
                GRANT SELECT, INSERT, DELETE
                    ON game_tag_assignments, game_platform_assignments
                    TO indiefi_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS game_platform_assignments_enforce_limit
                    ON game_platform_assignments;
                DROP TRIGGER IF EXISTS game_tag_assignments_enforce_limit
                    ON game_tag_assignments;
                DROP FUNCTION IF EXISTS enforce_game_platform_assignment_limit();
                DROP FUNCTION IF EXISTS enforce_game_tag_assignment_limit();

                REVOKE ALL ON game_genres, game_tags, game_platforms FROM indiefi_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON game_genres, game_tags, game_platforms
                    TO indiefi_app;

                REVOKE ALL ON game_tag_assignments, game_platform_assignments FROM indiefi_app;
                GRANT SELECT, INSERT, UPDATE, DELETE
                    ON game_tag_assignments, game_platform_assignments
                    TO indiefi_app;
                """);

            migrationBuilder.DropIndex(
                name: "IX_game_tags_active_sort",
                table: "game_tags");

            migrationBuilder.DropIndex(
                name: "IX_game_tags_code",
                table: "game_tags");

            migrationBuilder.DropIndex(
                name: "IX_game_tags_display_name",
                table: "game_tags");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_tags_code_format",
                table: "game_tags");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_tags_display_name_format",
                table: "game_tags");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_tags_sort_order_range",
                table: "game_tags");

            migrationBuilder.DropIndex(
                name: "IX_game_platforms_active_sort",
                table: "game_platforms");

            migrationBuilder.DropIndex(
                name: "IX_game_platforms_code",
                table: "game_platforms");

            migrationBuilder.DropIndex(
                name: "IX_game_platforms_display_name",
                table: "game_platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_platforms_code_format",
                table: "game_platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_platforms_display_name_format",
                table: "game_platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_platforms_sort_order_range",
                table: "game_platforms");

            migrationBuilder.DropIndex(
                name: "IX_game_genres_active_sort",
                table: "game_genres");

            migrationBuilder.DropIndex(
                name: "IX_game_genres_code",
                table: "game_genres");

            migrationBuilder.DropIndex(
                name: "IX_game_genres_display_name",
                table: "game_genres");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_genres_code_format",
                table: "game_genres");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_genres_display_name_format",
                table: "game_genres");

            migrationBuilder.DropCheckConstraint(
                name: "CK_game_genres_sort_order_range",
                table: "game_genres");
        }
    }
}
