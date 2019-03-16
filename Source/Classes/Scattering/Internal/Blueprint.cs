﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using RimWorld;
using Verse;

/**
 * Each object of this class represents one single blueprint in mod's internal format. It is partially internal classes, partially raw xml.
 * This class also provides related information like blueprint total cost, blueprint defence ability, blueprint size and so on.
 * */

namespace RealRuins {
    class Blueprint {

        // ------------ blueprint internal data structures --------------
        public int width { get; private set; }
        public int height { get; private set; }
        public readonly Version version;

        public float totalCost { get; private set; }
        public float itemsDensity { get; private set; }

        //rooms info. used to create deterioration maps.
        //earlier it was used by deterioration processor only, but now I hsve a few more places where I need this data, so I moved it to the blueprint level
        public int roomsCount { get; private set; }
        public List<int> roomAreas { get; private set; }

        //year of the snapshot. used to calculate offset to make things look realistic (no corpses and art from the future)
        private int snapshotYearInt;
        public int snapshotYear {
            get => snapshotYearInt;
            set {
                snapshotYearInt = value;
                dateShift = -(value - 5500) - Rand.Range(5, 500);
            }
        }
        public int dateShift { get; private set; }

        //map of walls to create room-based deterioration. 0 means "not traversed", -1 means "wall or similar", otherwise it's a room number
        //where 1 is a virtual "outside" room. Technically it's possible to have no "outside" if in the original blueprint there is an
        //continous wall around the whole area. In this case room index 1 will be assigned to the first room traversed.
        public readonly int[,] wallMap;
        public readonly TerrainTile[,] terrainMap;
        public readonly bool[,] roofMap;
        public readonly List<ItemTile>[,] itemsMap;



        public Blueprint(int width, int height, Version version) {
            this.version = version;
            this.width = width;
            this.height = height;
            wallMap = new int[width, height];
            roofMap = new bool[width, height];
            itemsMap = new List<ItemTile>[width, height];
            terrainMap = new TerrainTile[width, height];
        }

        public void CutIfExceedsBounds(IntVec3 size) {
            if (width > size.x) width = size.x;
            if (height > size.z) height = size.z;
        }

        // -------------------- cost related methods --------------------

        //Calculates cost of item made of stuff, or default cost if stuff is null
        //Golden wall is a [Wall] made of [Gold], golden tile is a [GoldenTile] made of default material
        private float ThingComponentsMarketCost(BuildableDef buildable, ThingDef stuffDef = null) {
            float num = 0f;

            if (buildable == null) return 0; //can be for missing subcomponents, i.e. bed from alpha-poly. Bed does exist, but alpha poly does not.

            if (buildable.costList != null) {
                foreach (ThingDefCountClass cost in buildable.costList) {
                    num += (float)cost.count * ThingComponentsMarketCost(cost.thingDef);
                }
            }

            if (buildable.costStuffCount > 0) {
                if (stuffDef == null) {
                    stuffDef = GenStuff.DefaultStuffFor(buildable);
                }

                if (stuffDef != null) {
                    num += (float)buildable.costStuffCount * stuffDef.BaseMarketValue * (1.0f / stuffDef.VolumePerUnit);
                }
            }

            if (num == 0) {
                if (buildable is ThingDef) {
                    if (((ThingDef)buildable).recipeMaker == null) {
                        return ((ThingDef)buildable).BaseMarketValue; //on some reason base market value is calculated wrong for, say, golden walls
                    }
                }
            }
            return num;
        }

        public void UpdateBlueprintStats(bool includeCost = false) {
            totalCost = 0;
            int itemsCount = 0;
            for (int x = 0; x < width; x ++) {
                for (int z = 0; z < height; z ++) {
                    var items = itemsMap[x, z];
                    if (items == null) continue;
                    foreach (ItemTile item in items) {
                        ThingDef thingDef = DefDatabase<ThingDef>.GetNamed(item.defName, false);
                        ThingDef stuffDef = (item.stuffDef != null) ? DefDatabase<ThingDef>.GetNamed(item.stuffDef, false) : null;
                        if (thingDef == null) continue; //to handle corpses

                        if (includeCost) {
                            try {
                                //Since at this moment we don't have filtered all things, we can't be sure that cost for all items can be calculated
                                item.cost = ThingComponentsMarketCost(thingDef, stuffDef) * item.stackCount;
                                totalCost += item.cost * item.stackCount;
                            } catch (Exception) { } //just ignore items with uncalculatable cost
                        }

                        item.weight = thingDef.GetStatValueAbstract(StatDefOf.Mass, stuffDef);
                        //Debug.Message("Getting weight of {0} made of {1} : {2}", thingDef.defName, stuffDef?.defName, item.weight);
                        if (item.stackCount != 0) item.weight *= item.stackCount;
                        if (item.weight == 0) {
                            if (item.stackCount != 0) {
                                item.weight = 0.5f * item.stackCount;
                            } else {
                                item.weight = 1.0f;
                            }
                        }

                        itemsCount++;
                    }

                    var terrainTile = terrainMap[x, z];
                    if (terrainTile != null) {
                        TerrainDef terrainDef = DefDatabase<TerrainDef>.GetNamed(terrainTile.defName, false);
                        if (terrainDef != null && includeCost) {
                            try {
                                terrainTile.cost = ThingComponentsMarketCost(terrainDef);
                                totalCost += terrainTile.cost;
                            } catch (Exception) { }
                        }
                    }
                }
            }
            itemsDensity = (float)itemsCount / (width * height);
        }

        // -------------- walls processing ------------
        //Wall map management: wall map is used to determine which rooms are opened and which are not. Similar to game engine regions, but much simplier and smaller.
        public void MarkRoomAsOpenedAt(int posX, int posZ) {
            int value = wallMap[posX, posZ];
            if (value < 2) return; //do not re-mark walls, uncalculated and already marked

            for (int x = 0; x < width; x++) {
                for (int z = 0; z < height; z++) {
                    if (wallMap[x, z] == value) wallMap[x, z] = -1;
                }
            }
        }

        //This method affects wall map only, it does not actually remove a wall
        public void RemoveWall(int posX, int posZ) {
            if (posX < 0 || posZ < 0 || posX >= width || posZ >= height) return;
            if (wallMap[posX, posZ] != -1) return; //alerady no wall there
            int? newValue = null;

            //determine new value. if we're on the edge, the room will be opened
            if (posX == 0 || posX == width - 1 || posZ == 0 || posZ == height - 1) {
                newValue = 1;
            }

            List<int> adjacentRoomNumbers = new List<int>();
            if (posX > 0) adjacentRoomNumbers.Add(wallMap[posX - 1, posZ]);
            if (posX < width - 1) adjacentRoomNumbers.Add(wallMap[posX + 1, posZ]);
            if (posZ > 0) adjacentRoomNumbers.Add(wallMap[posX, posZ - 1]);
            if (posZ < height - 1) adjacentRoomNumbers.Add(wallMap[posX, posZ + 1]);
            adjacentRoomNumbers.RemoveAll((int room) => room == -1);
            List<int> distinct = adjacentRoomNumbers.Distinct().ToList();
            // Debug.Message("Combining rooms: {0}", distinct);
            if (newValue == null && distinct.Count > 0) {
                if (distinct.Contains(1)) {
                    distinct.Remove(1);
                    newValue = 1;
                } else {
                    newValue = distinct.Pop();
                }
            }

            if (distinct.Count > 0) {
                for (int x = 0; x < width; x++) {
                    for (int z = 0; z < height; z++) {
                        if (distinct.Contains(wallMap[x, z])) wallMap[x, z] = newValue ?? 1;
                    }
                }
            }
        }

        public void FindRooms() {
            int currentRoomIndex = 1;
            roomAreas = new List<int>() { 0 }; //we don't have a room indexed zero, so place it here as if it were processed already

            void TraverseCells(List<IntVec3> points) { //BFS
                int area = 0;
                List<IntVec3> nextLevel = new List<IntVec3>();
                foreach (IntVec3 point in points) {
                    if (point.x < 0 || point.z < 0 || point.x >= width || point.z >= height) continue; //ignore out of bounds
                    if (wallMap[point.x, point.z] != 0) continue; //ignore processed points

                    wallMap[point.x, point.z] = currentRoomIndex;
                    area++;

                    nextLevel.Add(new IntVec3(point.x - 1, 0, point.z));
                    nextLevel.Add(new IntVec3(point.x + 1, 0, point.z));
                    nextLevel.Add(new IntVec3(point.x, 0, point.z - 1));
                    nextLevel.Add(new IntVec3(point.x, 0, point.z + 1));
                }

                if (roomAreas.Count == currentRoomIndex) {
                    roomAreas.Add(0);
                }

                roomAreas[currentRoomIndex] += area;

                if (nextLevel.Count > 0) {
                    TraverseCells(nextLevel);
                }
            }

            //For each unmarked point we can interpret our map as a tree with root at current point and branches going to four directions. For this tree (with removed duplicate nodes) we can implement BFS traversing.
            for (int z = 0; z < height; z++) {
                for (int x = 0; x < width; x++) {
                    if (wallMap[x, z] == 0) {
                        TraverseCells(new List<IntVec3>() { new IntVec3(x, 0, z) });
                        currentRoomIndex += 1;
                    }
                }
            }

            roomsCount = currentRoomIndex;
            Debug.Message("Traverse completed. Found {0} rooms", currentRoomIndex);
        }
    }
}