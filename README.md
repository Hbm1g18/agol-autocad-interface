# CADGIS

This plugin allows for GIS layer integration into CAD from ArcGIS online hosted layers or PostgreSQL PostGIS database sources. The plugin is under development with more features being added such as appendix generation, working with raster formats and open data sources (opentopo, osm etc.)


# Generating an appendix

The photo appendix button allows for a set template to be used in order to generate a fixed layout with dynamic data entry.
For this to work, a template must first be set up within the appendix settings window.
This will link to a .DWT file with the layout required.

On this template it is required to have a blockdef with name **APPENDIX_TEMPLATE** and an attdef of **COMMENT**.
This is what the plugin will look for an input the data into.

In the future I would like to implement a dynamic comment generation perhaps using a SQL phrase builder and inset map generation alongside this.

# Open data

This plugin is developed from a British viewpoint and so may be heavily focused on open data sets that benefit users within Britain. As such, I am looking to implement support for easy loading of [Defra survey data](https://environment.data.gov.uk/survey) within AutoCAD.
I am also interested in bringing support for OS map data using the API.

On a wider scale, OSM data and OpenTopography would be interesting/useful to bring support into this tool.
